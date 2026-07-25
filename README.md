# Taskbar Music Widget

A now-playing widget that sits at the **left end of the Windows 11 taskbar**, where the Widgets (news and weather) button normally lives. It shows album art, title, artist, and an Apple-Music-style bar visualiser driven by the audio actually playing.

The goal is for it to read as part of Windows rather than as a third-party add-on.

![The widget on the taskbar](docs/screenshot.png)

## Requirements

- Windows 11 (developed against 25H2, build 26200)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — or the SDK to build

## Setup

Free the space first by turning the Widgets button off:

**Settings → Personalization → Taskbar → Taskbar items → Widgets: Off**

Then build and run:

```bash
dotnet run --project src/TaskbarMusicWidget
```

Right-click the widget for **Start with Windows**, **Show visualizer**, **Visualizer color**, **Restart visualizer**, and **Exit**.

The same menu is on the notification-area icon, though Windows 11 files new tray icons into the overflow (the `⌄` chevron) by default — drag it onto the taskbar to keep it visible. Right-clicking the widget is the more reliable route.

## Settings

Settings live in `%LocalAppData%\TaskbarMusicWidget\settings.json`, written whenever you change something from the menu. A missing or malformed file falls back to defaults rather than failing to start, so it is safe to delete or hand-edit.

**Visualizer color** takes one of four values:

| Mode | Bars are… |
|---|---|
| `Default` | White on the dark taskbar, medium grey on the light one. |
| `SystemAccent` | Your Windows accent color. |
| `AlbumArt` | The dominant hue of the current cover, crossfading as tracks change. |
| `Custom` | A fixed color from `CustomBarColor` (`#RRGGBB`, `#AARRGGBB` or a color name). |

`Custom` has no menu entry yet — picking a color needs a picker, which is waiting on a settings window — but it works if you set it in the file.

Every mode except `Default` is **corrected for legibility** before it is drawn. The hue is the artwork's (or yours) to choose; the lightness is not. A dark navy cover on the dark taskbar or a pale yellow one on the light taskbar would otherwise paint bars that are technically colored and practically invisible, so the hue is kept and the lightness is pushed until the bars clear a 3:1 contrast ratio — WCAG's threshold for non-text graphics — against the taskbar. Saturation is held inside a band for the same reason, so a correction can neither bleach the hue to grey nor push it into neon. A cover with no usable hue at all, like a black-and-white sleeve, falls back to `Default` rather than inventing a tint.

## How it works

Windows 11 removed the DeskBand API, so there is no supported way to host content *inside* the taskbar. Instead this is a transparent, non-activating window that shadows the taskbar:

- **It paints no background.** The taskbar's own Mica/acrylic material shows through, so the widget inherits the system backdrop exactly and stays correct across theme, accent and transparency changes without reproducing any of it.
- **It is a satellite of `Shell_TrayWnd`**, mirroring that window's rect, DPI, visibility and z-order. One mechanism covers auto-hide, fullscreen apps, DPI changes and Explorer restarts. Auto-hide needs no special handling at all: the taskbar slides off-screen and the widget slides with it.
- **Metadata comes from SMTC** (`GlobalSystemMediaTransportControlsSessionManager`), the same source the Windows volume flyout reads — so Spotify, Apple Music, browsers and podcast apps work without per-app integration.
- **The visualiser** captures the system mix via WASAPI loopback, runs a Hann-windowed 1024-point FFT, and maps it into four log-spaced bands. Each band has its own dB window, because measured against real music the bands sit ~30 dB apart and a shared window leaves bass pinned near maximum and treble stuck at zero.
- **The capture self-heals.** A loopback capture stays bound to one device and doesn't follow the default as it moves, and after idle or sleep it can silently stall without notifying the app — both used to need a restart. A watchdog re-arms it when it has died, when the default output moves elsewhere (e.g. headphones reconnect after sleep), or when it goes quiet while a track is still playing. Capture is also re-armed on resume from sleep, and **Restart visualizer** in the menu forces it by hand.

Hovering crossfades the visualiser into previous / play-pause / next inside a fixed-width zone, so nothing reflows — layout shift on hover is the fastest way to look third-party.

## Project layout

```
tests/TaskbarMusicWidget.Tests/    colour maths, contrast floor, hue extraction
src/TaskbarMusicWidget/
├─ Shell/        window hosting, taskbar tracking, Win32 interop
├─ Media/        SMTC session handling and album art
├─ Audio/        WASAPI loopback capture and FFT
├─ Ui/           theme tokens, colour resolution, the visualiser control
├─ Settings/     the persisted settings model and its JSON store
├─ Tray/         notification-area icon and menu
├─ Startup/      run-at-sign-in registration
└─ Diagnostics/  opt-in logging, demo content
```

## Development

| Variable | Effect |
|---|---|
| `TMW_DEBUG=1` | Writes `%TEMP%\taskbar-music-widget.log` — taskbar state transitions, media sessions, and periodic visualiser band levels. |
| `TMW_DEMO=1` | Shows a synthetic track with generated cover art instead of reading SMTC. |
| `TMW_DEMO_TITLE` / `TMW_DEMO_ARTIST` | Override the demo track's title/artist (needs `TMW_DEMO=1`) — handy for checking the overflow fade at different text lengths. |

`TMW_DEMO` exists because the widget hides itself when nothing is playing, which otherwise makes the design impossible to inspect on a quiet machine.

Debugging the window layer interactively is awkward — attaching a debugger changes foreground-window behaviour, which is often the thing being observed. Prefer the log.

**Close the running widget before building.** It holds a lock on its own executable, and the build fails on the copy step rather than saying anything useful about why.

## Tests

```bash
dotnet test
```

The window and audio layers are verified by observation — they are about real windowing and device behaviour, and a mock of either would only assert that the mock works. What *is* covered is the part with a guarantee attached: the colour maths, the contrast floor, and hue extraction from cover art.

The contrast tests sweep 4536 hue/saturation/lightness inputs per theme and assert that anything with a hue to keep comes back clearing 3:1, that a real taskbar gets more headroom than the pessimistic estimate aims for, and that hue never moves by more than a few degrees. There are named cases for the ones that are easy to get wrong: saturated blue carries 7% of luminance and needs the most correction to be seen, a pale cover must not resolve to something indistinguishable from white, and a dark one must not paint near-black bars on the light taskbar.

These tests were checked by mutation — each guarantee was deliberately broken to confirm a test fails. That found two that did not: the sweep tolerated a correction that gave up early (returning "uncorrectable" is legal there, so the assertion never ran), and the letterboxing test used pure black, which the saturation gate already rejects, so it never exercised the lightness gate it was named for. Both are now covered. If you tune the constants in `Legibility` or `AlbumArtPalette`, the suite is what tells you whether you have made bars that cannot be seen.

## Known limitations

- **Primary monitor only.** Secondary-monitor taskbars (`Shell_SecondaryTrayWnd`) are not tracked yet.
- **Loopback captures the whole mix**, not just the media session on display, so other system audio moves the bars. Per-process loopback exists but needs a much heavier activation path.
- **Click-to-focus is best-effort.** SMTC identifies sessions only by AUMID; for packaged apps that maps to no process name, and Windows' foreground rules can refuse the activation regardless.
- Not signed. SmartScreen will warn on first run.
