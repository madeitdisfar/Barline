# Contributing

Pull requests are welcome.

**By opening one you agree that your contribution is licensed under GPL-3.0-or-later,
and that the maintainer may also release it under other terms** — which keeps the door
open to offering a commercial licence later without having to track down every past
contributor for permission.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project src/Barline
```

**Close the running widget before building.** It holds a lock on its own executable,
and the build fails on the copy step rather than saying anything useful about why.

A release build is self-contained and single-file, and the project is configured so
that a plain `dotnet publish -c Release` produces exactly what ships — the deployment
shape is not a flag you have to remember.

## Project layout

```
tests/Barline.Tests/    colour maths, contrast floor, hue extraction, lyric parsing
src/Barline/
├─ Shell/        window hosting, taskbar tracking, Win32 interop
├─ Media/        SMTC session handling, album art, the playback clock
├─ Audio/        WASAPI loopback capture and FFT
├─ Lyrics/       fetching, parsing, caching, word timing, appearance model
├─ Ui/           theme tokens, colour resolution, the visualiser control
├─ Settings/     the settings model, its JSON store, and the settings window
├─ Tray/         notification-area icon and menu
├─ Startup/      run-at-sign-in registration
└─ Diagnostics/  opt-in logging, demo content
```

The reasoning behind the design is in [docs/design.md](docs/design.md). Read it before
changing anything in `Audio/`, `Lyrics/` or the colour path — most of what looks
arbitrary there is load-bearing.

## Debugging

| Variable | Effect |
|---|---|
| `BARLINE_DEBUG=1` | Writes `%TEMP%\barline.log` — taskbar state transitions, media sessions, periodic visualiser band levels, and playback-clock accuracy. |
| `BARLINE_DEMO=1` | Shows a synthetic track with generated cover art instead of reading SMTC. |
| `BARLINE_DEMO_TITLE` / `BARLINE_DEMO_ARTIST` | Override the demo track's title/artist (needs `BARLINE_DEMO=1`) — handy for checking the overflow fade at different text lengths. |
| `BARLINE_SETTINGS=1` | Opens the settings window at startup, instead of right-clicking the tray on every rebuild. |

`BARLINE_DEMO` exists because the widget hides itself when nothing is playing, which
otherwise makes the design impossible to inspect on a quiet machine.

Debugging the window layer interactively is awkward — attaching a debugger changes
foreground-window behaviour, which is often the thing being observed. Prefer the log.

## Tests

```bash
dotnet test
```

The window and audio layers are verified by observation — they are about real
windowing and device behaviour, and a mock of either would only assert that the mock
works. What *is* covered is the part with a guarantee attached: the colour maths, the
contrast floor, hue extraction from cover art, lyric parsing, and the playback clock.

What is *not* covered is how the bars look, which is why the range on offer was chosen
by rendering the real control at counts 4–8 and magnifying the actual pixels without
interpolation, at both 100% and 200% scaling. No assertion can tell you a 2px bar
survives antialiasing.

The contrast tests sweep 4536 hue/saturation/lightness inputs per theme and assert
that anything with a hue to keep comes back clearing 3:1, that a real taskbar gets more
headroom than the pessimistic estimate aims for, and that hue never moves by more than
a few degrees. There are named cases for the ones that are easy to get wrong:
saturated blue carries 7% of luminance and needs the most correction to be seen, a
pale cover must not resolve to something indistinguishable from white, and a dark one
must not paint near-black bars on the light taskbar.

The bar-count tests hold the two sizing invariants — every count occupies the same
width and paints the same amount of ink — and pin the default to the original 3px bar,
so generalising the rule cannot quietly restyle it. On the audio side they hold the
fitted dB windows to within 2 dB of the four that were measured by hand, and assert
that every supported count still gives each band an FFT bin of its own. One test
deliberately asserts that *seven* bands do not, so the reason the range stops at six is
checked rather than just commented.

### Checked by mutation

These tests were checked by mutation — each guarantee was deliberately broken to
confirm a test fails. That found two that did not: the sweep tolerated a correction
that gave up early (returning "uncorrectable" is legal there, so the assertion never
ran), and the letterboxing test used pure black, which the saturation gate already
rejects, so it never exercised the lightness gate it was named for. Both are now
covered.

If you tune the constants in `Legibility` or `AlbumArtPalette`, the suite is what tells
you whether you have made bars that cannot be seen.
