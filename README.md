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

Right-click the widget for **Start with Windows**, **Show visualizer**, and **Exit**.

The same menu is on the notification-area icon, though Windows 11 files new tray icons into the overflow (the `⌄` chevron) by default — drag it onto the taskbar to keep it visible. Right-clicking the widget is the more reliable route.

## How it works

Windows 11 removed the DeskBand API, so there is no supported way to host content *inside* the taskbar. Instead this is a transparent, non-activating window that shadows the taskbar:

- **It paints no background.** The taskbar's own Mica/acrylic material shows through, so the widget inherits the system backdrop exactly and stays correct across theme, accent and transparency changes without reproducing any of it.
- **It is a satellite of `Shell_TrayWnd`**, mirroring that window's rect, DPI, visibility and z-order. One mechanism covers auto-hide, fullscreen apps, DPI changes and Explorer restarts. Auto-hide needs no special handling at all: the taskbar slides off-screen and the widget slides with it.
- **Metadata comes from SMTC** (`GlobalSystemMediaTransportControlsSessionManager`), the same source the Windows volume flyout reads — so Spotify, Apple Music, browsers and podcast apps work without per-app integration.
- **The visualiser** captures the system mix via WASAPI loopback, runs a Hann-windowed 1024-point FFT, and maps it into four log-spaced bands. Each band has its own dB window, because measured against real music the bands sit ~30 dB apart and a shared window leaves bass pinned near maximum and treble stuck at zero.

Hovering crossfades the visualiser into previous / play-pause / next inside a fixed-width zone, so nothing reflows — layout shift on hover is the fastest way to look third-party.

## Project layout

```
src/TaskbarMusicWidget/
├─ Shell/        window hosting, taskbar tracking, Win32 interop
├─ Media/        SMTC session handling and album art
├─ Audio/        WASAPI loopback capture and FFT
├─ Ui/           theme tokens and the visualiser control
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

## Known limitations

- **Primary monitor only.** Secondary-monitor taskbars (`Shell_SecondaryTrayWnd`) are not tracked yet.
- **Loopback captures the whole mix**, not just the media session on display, so other system audio moves the bars. Per-process loopback exists but needs a much heavier activation path.
- **Click-to-focus is best-effort.** SMTC identifies sessions only by AUMID; for packaged apps that maps to no process name, and Windows' foreground rules can refuse the activation regardless.
- Not signed. SmartScreen will warn on first run.
