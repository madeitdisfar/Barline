<p align="center">
  <img src="docs/icon.png" width="128" alt="Barline">
</p>

<h1 align="center">Barline</h1>

<p align="center">
  <b>A now-playing widget for the Windows 11 taskbar.</b><br>
  Barline displays album art, track information, a live audio visualizer, and synchronized lyrics directly on the taskbar.
</p>

<p align="center">
  <a href="https://github.com/madeitdisfar/Barline/actions/workflows/ci.yml"><img src="https://github.com/madeitdisfar/Barline/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg" alt="License: GPL v3"></a>
</p>

<p align="center">
  <img src="docs/main-screenshot.png" width="840" alt="Barline on the Windows 11 taskbar">
</p>

It is designed to integrate seamlessly into the Windows environment. It paints
no background of its own, so the taskbar's real Mica material shows through automatically. It always matches your theme, accent color, transparency, and wallpapers without imitation.

<p align="center">
  <img src="docs/screenshot.png" width="560" alt="Now playing on the taskbar"><br>
  <sub>Now playing</sub>
  <br><br>
  <img src="docs/screenshot-2.png" width="560" alt="Transport controls on hover"><br>
  <sub>Hover for transport controls</sub>
</p>

## Features

### Seamless taskbar widget 🎵

- Displays album art, title, and artist directly on the taskbar.
- Fixed layout that remains consistent as songs change.
- Follows taskbar position, DPI, and auto-hide behavior.

### Live audio visualizer 📊

- Driven by real WASAPI loopback audio, not a pre-rendered animation.
- Choose the number of bars.
- Bar colors can follow Windows accent, album artwork or custom colors of your choice.

### Synchronized lyrics 🎤

- Floating lyrics panel or inline display.
- Synchronized lyrics from LRCLIB.
- Word-by-word highlighting on any synced track.
- Import local `.lrc` files for any track.

### Highly customizable 🎨

- Font, size, weight, color, effects, background and corner radius, and more.
- Save and import appearance presets.
- Changes apply instantly.

### Works everywhere 🎧

Supports any player that integrates with Windows Media Controls, including:

- Spotify
- Apple Music
- Chromium-based browsers
- ...and many others

### Privacy first 🔒

- No telemetry.
- No accounts.
- No network access unless lyrics are enabled.

## Install

Requires **Windows 11**. Download the latest release, unzip it anywhere, and run
`Barline.exe`. The release build carries its own runtime, so there is nothing else to
install.

**[➡️ Download the latest release](https://github.com/madeitdisfar/Barline/releases/latest)**

The first launch explains the rest, including this, which only matters if Windows is
still showing its own button in the same corner:

> **Settings → Personalization → Taskbar → Taskbar items → Widgets: Off**

Note that the widget hides itself while nothing is playing, so a quiet machine looks
exactly as it did before. Start a track and it appears.

Right-click the widget for **Settings**, **Show visualizer**, **Restart visualizer**
and **Exit**. The same menu is on the notification-area icon, which is where to reach
it while the widget is hidden.

To build it yourself instead, see [CONTRIBUTING.md](CONTRIBUTING.md).

## Lyrics

Lyrics are disabled by default because looking up a track sends its title and artist to [LRCLIB](https://lrclib.net). Turn it on under **Lyrics → Show lyrics**.

They go either in a **floating panel** above the taskbar, with room for a full line and
a look you choose:

<table>
<tr>
<td width="50%" align="center"><img src="docs/lyrics-panel.png" alt="Example 1"></td>
<td width="50%" align="center"><img src="docs/lyrics-panel-2.png" alt="Example 2"></td>
</tr>
</table>

Or **inside the widget**, replacing the title until you hover it:

<p align="center">
  <img src="docs/lyrics-inline.png" width="560" alt="Lyrics inside the widget">
</p>

The panel passes clicks straight through, hides for fullscreen apps, and slides away
with auto-hide.

Word-level timing is estimated from syllable count, including for Hangul, kana and CJK,
which are counted per character rather than by vowel groups.

Got better timings? Use **Import for this track…** in settings, or drop an `.lrc` file
into the lyrics folder yourself — **Open folder** beside the button gets you there. A
file always takes priority over the network, and clearing the cache never touches it.

## Settings

<p align="center">
  <img src="docs/settings.png" width="520" alt="The settings window">
</p>

Everything applies immediately. There is no OK button, and the widget is on screen the
whole time, so every change is previewed where it counts.

Bar color follows your **accent**, the **album art** (crossfading as tracks change),
or a color you choose. Bar colors automatically adjust to maintain a minimum 3:1 contrast ratio against the taskbar, ensuring the visualizer remains visible regardless of album artwork. The settings window shows the color that will actually be drawn.

Lyric styles are saved as **preset files**. Four built-in presets ship with the app;
drop one someone sends you into the presets folder and it appears in the list. Use
**Open folder** next to the preset picker to get there, since the location differs
between the portable and Microsoft Store builds.

**About** at the bottom of the page shows the version and which build it is, and opens
the license, the third-party notices and the data folder. That is the only route to any
of them in the Store build, whose install folder Windows will not let you browse.

## Known limitations

- **Primary monitor only.** Secondary-monitor taskbars are not tracked yet.
- **The visualizer hears everything**, not just the media session on display, so other
  system audio moves the bars.
- **Click-to-focus is best-effort.** Windows identifies media sessions in a way that
  does not always map back to a window it will agree to activate.
- **Not signed.** SmartScreen will warn on first run.

## Documentation

- **[📖 Design notes](docs/design.md)**
- **[🤝 Contributing](CONTRIBUTING.md)**
- **[🔒 Privacy](PRIVACY.md)**

## License

Licensed under [GPL-3.0-or-later](LICENSE).

Third-party component licenses are available in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Both files ship beside the executable in every build, and **Settings → About** opens
them without leaving the app.
