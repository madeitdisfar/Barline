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

Right-click the widget for **Settings**, **Show visualizer**, **Restart visualizer**, and **Exit**. The menu carries quick actions only; anything that is configuration rather than a one-off lives in the settings window, so no state is shown in two places where it can drift.

The same menu is on the notification-area icon, though Windows 11 files new tray icons into the overflow (the `⌄` chevron) by default — drag it onto the taskbar to keep it visible. Right-clicking the widget is the more reliable route.

## Settings

Changes apply and persist immediately — there is no OK or Apply button, matching Windows 11 Settings, and the widget is on the taskbar the whole time so every change is already previewed where it counts.

The file lives at `%LocalAppData%\TaskbarMusicWidget\settings.json`. A missing or malformed file falls back to defaults rather than failing to start, so it is safe to delete or hand-edit, and it is written via a temp file and a swap so a crash mid-write cannot truncate it.

**Bar color** takes one of four values:

| Mode | Bars are… |
|---|---|
| `Default` | White on the dark taskbar, medium grey on the light one. |
| `SystemAccent` | Your Windows accent color. |
| `AlbumArt` | The dominant hue of the current cover, crossfading as tracks change. |
| `Custom` | A color you pick, from a hue palette or as `#RRGGBB` / `#AARRGGBB` / a color name. |

**Bars** chooses how many bars are drawn — Simple (4), Balanced (5) or Detailed (6). They divide a fixed width *and* a fixed amount of ink, so a higher count means thinner bars rather than a wider or heavier visualiser: the widget never reflows the taskbar layout, and the row keeps the visual weight its colour was corrected for.

Six is the ceiling for two independent reasons. Seven bars would be 1.7px wide, and neighbouring tall bars visibly merge at 100% display scaling — worse on the light taskbar, where the bar colour is translucent as well as thin. Seven bands would also outrun the transform: a 1024-point FFT at 48 kHz resolves 46.9 Hz per bin, and the lowest band's 40–89.8 Hz span falls inside the single bin its neighbour starts from, so the bottom two bars would carry identical data and move as one.

Every mode except `Default` is **corrected for legibility** before it is drawn. The hue is the artwork's (or yours) to choose; the lightness is not. A dark navy cover on the dark taskbar or a pale yellow one on the light taskbar would otherwise paint bars that are technically colored and practically invisible, so the hue is kept and the lightness is pushed until the bars clear a 3:1 contrast ratio — WCAG's threshold for non-text graphics — against the taskbar. Saturation is held inside a band for the same reason, so a correction can neither bleach the hue to grey nor push it into neon. A cover with no usable hue at all, like a black-and-white sleeve, falls back to `Default` rather than inventing a tint.

Because of that, the settings window shows each mode's **resolved** color and hex — what will actually be drawn, not what was picked — over a strip painted the same shade the correction measures against. Showing the picked color instead would misrepresent the whole feature.

## Lyrics

**Off by default.** A lookup sends the track's title and artist to [LRCLIB](https://lrclib.net), so it is opt-in rather than something the widget starts doing on its own. Turn it on under **Lyrics → Show lyrics**.

There are two places to put them:

| Mode | Where |
|---|---|
| `Inline` | In the widget itself, replacing the title until you hover it. Costs no extra window, but the reserved width is about 150px — twenty-five characters — so a long line is cut short. |
| `Panel` | A panel floating just above the taskbar, showing the line before and after as well. |

Inline, instrumental gaps are timed too, and during one the title returns rather than the previous line hanging around.

The panel uses Windows' own **acrylic material** rather than trying to adapt to the desktop behind it. Sampling the screen would cost a capture every frame and is self-referential — the window would capture itself — whereas the compositor blurs and tints for free on the GPU, and bounds the luminance the text has to survive to roughly the same range the taskbar occupies. That means the same contrast correction the bars use applies unchanged, and the panel reads as a system surface. Where the material is unavailable (before Windows 11 22H2) it paints a solid panel of the same shade instead, so the contrast guarantee holds either way.

The current line is drawn in the album art's colour, at a size that qualifies as large text — which is what makes the 3:1 ratio the correction guarantees the right threshold for it. The panel never takes input: clicks pass straight through to whatever is behind, and it is owned by the taskbar, so it hides for fullscreen apps and slides away with auto-hide by the same mechanism the widget does.

### Word by word

The panel highlights each word as it is sung. No free source carries word-level timing, so it is inferred: the line's span is divided by **syllable count**, which tracks singing time far better than character count — *strength* and *a potato* are the same length and nothing alike to sing. Vowel-group counting is an English heuristic, so scripts that write a syllable per character (Hangul, kana, CJK) are counted that way instead; otherwise a Korean line would come back as one syllable per word and the sweep would be worthless. A file that carries real word timings always overrides the estimate.

Aligning the words to the audio properly was considered and rejected — not because it is slow, but because it is **causal**. A forced aligner cannot know where a word lands until after it has been sung, so using one would mean running the lyrics behind the music, which defeats the point at any speed.

Three styles, all of which sweep:

| Style | Look |
|---|---|
| `Clean` | The album art's colour on acrylic. No effects. |
| `Glow` | Adds a soft halo behind the current line. |
| `Lime` | Flat lime panel, condensed black lowercase — the lo-fi album-cover look. |

Effects are carried on a **separate, bitmap-cached layer** behind the live text rather than on the text itself. An effect on the text would be re-rendered every frame as the highlight moves; on a static layer it rasterises once per line. Measured with the visualiser and the sweep both running, the whole widget sits near 2% CPU, so there is no performance trade-off to warn about.

LRCLIB was chosen because it is the only free source serving timed lyrics with no API key, no paid tier, and no terms forbidding this use. It is run at no charge for exactly this purpose, which is a reason to be a careful client: **every result is cached to disk, misses included**, so a track is fetched once rather than once per play. Misses expire after two weeks, since the database grows and today's gap may be filled next month.

Matching is the hard part, not coverage. Spotify reports `Creep - Remastered 2011` and a browser reports `Creep (Official Video)`; neither string is filed under that name anywhere. Lookups therefore widen in stages — the name exactly as reported first, since stripping is a guess and a track really can be called `Live`, then with packaging removed, then with only the first credited artist, and finally a duration-matched search that tolerates a source reporting the length a second or two out.

To supply your own, drop an `.lrc` file named `Artist - Title.lrc` into `%LocalAppData%\TaskbarMusicWidget\lyrics`. A file always wins over the network — it is the only route for tracks the database has never heard of, and the way to fix timings you disagree with. Both standard and word-level (enhanced) LRC are read.

## How it works

Windows 11 removed the DeskBand API, so there is no supported way to host content *inside* the taskbar. Instead this is a transparent, non-activating window that shadows the taskbar:

- **It paints no background.** The taskbar's own Mica/acrylic material shows through, so the widget inherits the system backdrop exactly and stays correct across theme, accent and transparency changes without reproducing any of it.
- **It is a satellite of `Shell_TrayWnd`**, mirroring that window's rect, DPI, visibility and z-order. One mechanism covers auto-hide, fullscreen apps, DPI changes and Explorer restarts. Auto-hide needs no special handling at all: the taskbar slides off-screen and the widget slides with it.
- **Metadata comes from SMTC** (`GlobalSystemMediaTransportControlsSessionManager`), the same source the Windows volume flyout reads — so Spotify, Apple Music, browsers and podcast apps work without per-app integration.
- **The visualiser** captures the system mix via WASAPI loopback, runs a Hann-windowed 1024-point FFT, and maps it into log-spaced bands, one per bar. Each band has its own dB window, because measured against real music the bands sit ~30 dB apart and a shared window leaves bass pinned near maximum and treble stuck at zero. Those windows started as four hand-measured values, which described one bar count and no other; they are now a least-squares fit through those measurements (about −4.4 dB per octave), which is what lets the count vary. The fit generalises because a band's level is the RMS *per bin* — a spectral density rather than a total — so slicing the span more finely does not systematically lower it.
- **Playback position is extrapolated, not read.** SMTC does not tick: a source app publishes a position when it feels like it — measured against Spotify, roughly every 4.3 seconds. Anything that needs to know where playback is *right now* has to carry the last report forward using the app's own timestamp, and re-anchor when the next one lands. Corrections are eased in rather than applied outright, so ordinary drift does not visibly step, while a disagreement large enough to be a seek is applied at once. Each report doubles as a measurement of the extrapolation before it, logged under `TMW_DEBUG`.

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
├─ Settings/     the settings model, its JSON store, and the settings window
├─ Tray/         notification-area icon and menu
├─ Startup/      run-at-sign-in registration
└─ Diagnostics/  opt-in logging, demo content
```

## Development

| Variable | Effect |
|---|---|
| `TMW_DEBUG=1` | Writes `%TEMP%\taskbar-music-widget.log` — taskbar state transitions, media sessions, periodic visualiser band levels, and playback-clock accuracy. |
| `TMW_DEMO=1` | Shows a synthetic track with generated cover art instead of reading SMTC. |
| `TMW_DEMO_TITLE` / `TMW_DEMO_ARTIST` | Override the demo track's title/artist (needs `TMW_DEMO=1`) — handy for checking the overflow fade at different text lengths. |
| `TMW_SETTINGS=1` | Opens the settings window at startup, instead of right-clicking the tray on every rebuild. |

`TMW_DEMO` exists because the widget hides itself when nothing is playing, which otherwise makes the design impossible to inspect on a quiet machine.

Debugging the window layer interactively is awkward — attaching a debugger changes foreground-window behaviour, which is often the thing being observed. Prefer the log.

**Close the running widget before building.** It holds a lock on its own executable, and the build fails on the copy step rather than saying anything useful about why.

## Tests

```bash
dotnet test
```

The window and audio layers are verified by observation — they are about real windowing and device behaviour, and a mock of either would only assert that the mock works. What *is* covered is the part with a guarantee attached: the colour maths, the contrast floor, and hue extraction from cover art.

What is *not* covered is how the bars look, which is why the range on offer was chosen by rendering the real control at counts 4–8 and magnifying the actual pixels without interpolation, at both 100% and 200% scaling. No assertion can tell you a 2px bar survives antialiasing.

The contrast tests sweep 4536 hue/saturation/lightness inputs per theme and assert that anything with a hue to keep comes back clearing 3:1, that a real taskbar gets more headroom than the pessimistic estimate aims for, and that hue never moves by more than a few degrees. There are named cases for the ones that are easy to get wrong: saturated blue carries 7% of luminance and needs the most correction to be seen, a pale cover must not resolve to something indistinguishable from white, and a dark one must not paint near-black bars on the light taskbar.

The bar-count tests hold the two sizing invariants — every count occupies the same width and paints the same amount of ink — and pin the default to the original 3px bar, so generalising the rule cannot quietly restyle it. On the audio side they hold the fitted dB windows to within 2 dB of the four that were measured by hand, and assert that every supported count still gives each band an FFT bin of its own. One test deliberately asserts that *seven* bands do not, so the reason the range stops at six is checked rather than just commented.

These tests were checked by mutation — each guarantee was deliberately broken to confirm a test fails. That found two that did not: the sweep tolerated a correction that gave up early (returning "uncorrectable" is legal there, so the assertion never ran), and the letterboxing test used pure black, which the saturation gate already rejects, so it never exercised the lightness gate it was named for. Both are now covered. If you tune the constants in `Legibility` or `AlbumArtPalette`, the suite is what tells you whether you have made bars that cannot be seen.

## Known limitations

- **Primary monitor only.** Secondary-monitor taskbars (`Shell_SecondaryTrayWnd`) are not tracked yet.
- **Loopback captures the whole mix**, not just the media session on display, so other system audio moves the bars. Per-process loopback exists but needs a much heavier activation path.
- **Click-to-focus is best-effort.** SMTC identifies sessions only by AUMID; for packaged apps that maps to no process name, and Windows' foreground rules can refuse the activation regardless.
- Not signed. SmartScreen will warn on first run.
