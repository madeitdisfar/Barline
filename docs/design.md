# Design notes

Why Barline works the way it does. This is the reasoning behind the decisions — the
constraints that were hit, the approaches that were rejected, and the two occasions
Windows silently ignored a perfectly valid API call.

For building and testing, see [CONTRIBUTING.md](../CONTRIBUTING.md).

---

## The window

Windows 11 removed the DeskBand API, so there is no supported way to host content
*inside* the taskbar. Instead this is a transparent, non-activating window that
shadows the taskbar:

- **It paints no background.** The taskbar's own Mica/acrylic material shows through,
  so the widget inherits the system backdrop exactly and stays correct across theme,
  accent and transparency changes without reproducing any of it.
- **It is a satellite of `Shell_TrayWnd`**, mirroring that window's rect, DPI,
  visibility and z-order. One mechanism covers auto-hide, fullscreen apps, DPI changes
  and Explorer restarts. Auto-hide needs no special handling at all: the taskbar
  slides off-screen and the widget slides with it.

Hovering crossfades the visualizer into previous / play-pause / next inside a
fixed-width zone, so nothing reflows — layout shift on hover is the fastest way to
look third-party.

## Media

**Metadata comes from SMTC** (`GlobalSystemMediaTransportControlsSessionManager`), the
same source the Windows volume flyout reads — so Spotify, Apple Music, browsers and
podcast apps work without per-app integration.

**Playback position is extrapolated, not read.** SMTC does not tick: a source app
publishes a position when it feels like it — measured against Spotify, roughly every
4.3 seconds. Anything that needs to know where playback is *right now* has to carry
the last report forward using the app's own timestamp, and re-anchor when the next one
lands. Corrections are eased in rather than applied outright, so ordinary drift does
not visibly step, while a disagreement large enough to be a seek is applied at once.
Each report doubles as a measurement of the extrapolation before it, logged under
`BARLINE_DEBUG`.

## The visualizer

The visualizer captures the system mix via WASAPI loopback, runs a Hann-windowed
1024-point FFT, and maps it into log-spaced bands, one per bar.

Each band has its own dB window, because measured against real music the bands sit
~30 dB apart and a shared window leaves bass pinned near maximum and treble stuck at
zero. Those windows started as four hand-measured values, which described one bar
count and no other; they are now a least-squares fit through those measurements (about
−4.4 dB per octave), which is what lets the count vary. The fit generalizes because a
band's level is the RMS *per bin* — a spectral density rather than a total — so
slicing the span more finely does not systematically lower it.

**The capture self-heals.** A loopback capture stays bound to one device and doesn't
follow the default as it moves, and after idle or sleep it can silently stall without
notifying the app — both used to need a restart. A watchdog re-arms it when it has
died, when the default output moves elsewhere (e.g. headphones reconnect after sleep),
or when it goes quiet while a track is still playing. Capture is also re-armed on
resume from sleep, and **Restart visualizer** in the menu forces it by hand.

### Why six bars is the ceiling

Bar counts divide a fixed width *and* a fixed amount of ink, so a higher count means
thinner bars rather than a wider or heavier visualizer: the widget never reflows the
taskbar layout, and the row keeps the visual weight its color was corrected for.

Six is the ceiling for two independent reasons. Seven bars would be 1.7px wide, and
neighboring tall bars visibly merge at 100% display scaling — worse on the light
taskbar, where the bar color is translucent as well as thin. Seven bands would also
outrun the transform: a 1024-point FFT at 48 kHz resolves 46.9 Hz per bin, and the
lowest band's 40–89.8 Hz span falls inside the single bin its neighbor starts from,
so the bottom two bars would carry identical data and move as one.

## Color and legibility

Every bar color mode except `Default` is **corrected for legibility** before it is
drawn. The hue is the artwork's (or yours) to choose; the lightness is not. A dark
navy cover on the dark taskbar or a pale yellow one on the light taskbar would
otherwise paint bars that are technically colored and practically invisible, so the
hue is kept and the lightness is pushed until the bars clear a 3:1 contrast ratio —
WCAG's threshold for non-text graphics — against the taskbar. Saturation is held
inside a band for the same reason, so a correction can neither bleach the hue to gray
nor push it into neon. A cover with no usable hue at all, like a black-and-white
sleeve, falls back to `Default` rather than inventing a tint.

Because of that, the settings window shows each mode's **resolved** color and hex —
what will actually be drawn, not what was picked — over a strip painted the same shade
the correction measures against. Showing the picked color instead would misrepresent
the whole feature.

## Lyrics

### Choosing a source

LRCLIB was chosen because it is the only free source serving timed lyrics with no API
key, no paid tier, and no terms forbidding this use. It is run at no charge for
exactly this purpose, which is a reason to be a careful client: **every result is
cached to disk, misses included**, so a track is fetched once rather than once per
play.

Misses are never cached permanently, though. LRCLIB is contributed, so a track with
nothing filed today is one nobody has got to yet — and a newly released song is
exactly the one most likely to be filled in shortly after you first ask for it. So a
miss is retried on a **widening delay**: the next day, then after three, seven and
thirty. That catches the common case quickly while stopping a library of instrumentals
from asking again on every play, and it means no track is ever written off for good.

### Matching, not coverage

Matching is the hard part. Spotify reports `Creep - Remastered 2011` and a browser
reports `Creep (Official Video)`; neither string is filed under that name anywhere.
Lookups therefore widen in stages — the name exactly as reported first, since
stripping is a guess and a track really can be called `Live`, then with packaging
removed, then with only the first credited artist, and finally a duration-matched
search that tolerates a source reporting the length a second or two out.

### Where a line ends

Where a record has one, LRCLIB's own **lyricsfile** form is preferred over the LRC. It
is the same lyrics as YAML, and it states each line's *end* — which LRC cannot
express, leaving a line to implicitly run until the next one starts. That is wrong
across every instrumental gap, and it is precisely the span the word timing divides
up. The format is documented as supporting word-level timing as well, but no
contributed data appears to use it yet: a sample of roughly a hundred records carried
only line-level fields, so nothing here guesses at a schema that isn't in the data.

### Word by word

The panel highlights either **each word as it is sung** or the whole line at once,
whichever you prefer — word timing is estimated for almost every track, and on one it
fits badly a line at a time is calmer.

No free source carries word-level timing, so it is inferred: the line's span is
divided by **syllable count**, which tracks singing time far better than character
count — *strength* and *a potato* are the same length and nothing alike to sing.
Vowel-group counting is an English heuristic, so scripts that write a syllable per
character (Hangul, kana, CJK) are counted that way instead; otherwise a Korean line
would come back as one syllable per word and the sweep would be worthless. A file that
carries real word timings always overrides the estimate.

Aligning the words to the audio properly was considered and rejected — not because it
is slow, but because it is **causal**. A forced aligner cannot know where a word lands
until after it has been sung, so using one would mean running the lyrics behind the
music, which defeats the point at any speed.

### The panel

The panel is a genuinely transparent window: WPF gives it real per-pixel alpha, and
the background is painted over whatever is behind it at the opacity you choose.

It never takes input: clicks pass straight through to whatever is behind, and it is
owned by the taskbar, so it hides for fullscreen apps and slides away with auto-hide
by the same mechanism the widget does. Because it cannot be clicked away, it can
instead **fade or hide while the pointer is over it** — the pointer is polled rather
than handled as an event, since a click-through window never receives one.

It also **waits a few seconds before disappearing**. Between tracks there is a moment
with no lyrics — the old ones dropped, the new ones not yet fetched — and hiding
immediately made the panel blink out and back on every song change. Showing is never
delayed; only hiding. A source app closing is a different thing from a gap between
songs, so the panel goes when the widget does rather than waiting out that grace.

Position anchors are measured from the taskbar's own monitor, so the panel follows the
taskbar rather than assuming the primary screen. **Free** stores its position as a
share of the screen rather than in pixels, so it stays put when the resolution
changes, and is adjustable a tenth of a percent at a time — one percent is 26 physical
pixels on a wide screen, which is too coarse to line the panel up with anything.

In the widget, instrumental gaps are timed too, and during one the title returns
rather than the previous line hanging around. So does pausing: a lyric sitting there
over music nobody is playing reads as stuck, and what you want to know about a paused
track is what it is. Turning the visualizer off gives that reserved width back to the
text — 150px becomes 238px, over half as much again — since the zone beside it is then
drawing nothing.

## The style is the setting

Font family, size, weight and italic, text color, unsung-word opacity, casing,
effect, background and corner radius are all settings — **and so are where the lyrics
go, which anchor the panel uses, and how big it is.** A 20px line over a tinted panel
and a 12px line in the widget are different designs, and a look that does not say
which of the two it is describes nothing; keeping placement outside the style was what
made one preset mean two different things depending on where the lyrics happened to
be.

Deliberately *not* part of it: whether lyrics are on, whether they light up a word or
a line at a time, and what the panel does when hovered. Those are preferences about
behavior rather than descriptions of a look, and a preset someone shares with you has
no business changing them.

The settings window groups by **what a setting applies to**, not by what a preset
happens to save — those are different questions, and answering the second with the
first is what once put the display mode inside the style card while the settings that
react to it sat outside. Controls that a choice makes meaningless are hidden rather
than left to do nothing — an effect radius with no effect, an opacity for an opaque
fill, a panel size for lyrics that are not in a panel.

In the widget there is no background at all: it deliberately paints none so the
taskbar's own material shows through, and that is the decision the whole widget is
built around.

### Effects sit on their own layer

Effects are carried on a **separate, bitmap-cached layer** behind the live text rather
than on the text itself, in the widget as well as in the panel. Two reasons, and the
first is not performance: a blur applied to the text is not a glow, it is the same
glyphs out of focus. What reads as a glow is a sharp line sitting on a blurred copy of
itself. The second is that an effect on the live text would be re-rendered every frame
as the highlight moves, where a static layer rasterizes once per line. Measured with
the visualizer and the sweep both running, the whole widget sits near 2% CPU, so there
is no performance trade-off to warn about.

### Backgrounds, and the one that was removed

**Backgrounds** are `Tinted` (see-through color at the opacity you choose), `Solid`,
or `None`.

A compositor-blurred acrylic option existed briefly and was removed: Windows
composites that blur across the whole window rectangle, and a transparent window takes
its shape from per-pixel alpha rather than from a region, so acrylic could never
honor a corner radius. One background behaving differently from the rest was not
worth what it bought — every background is now painted by the app, and the corner
radius applies to all of them alike.

### Presets are files

Presets live in the `presets` folder under Barline's data directory (see
[Where data lives](#where-data-lives)). They are saved copies of the style,
not a separate source of truth: the settings window edits it directly and you see it
immediately, and a preset is that snapshot under a name. The alternative — the file
being the only home for these values — would mean every tweak was a file edit and the
UI could only pick between whole files.

Four looks ship as ordinary preset files: `Widget` for the inline display, and
`Clean`, `Glow` and `Lime` for the panel. They are written on first run and never
overwritten afterwards, so an edited built-in survives an update. That makes them
readable, copyable and editable, and means writing your own starts from a working
example rather than an empty file. Drop a preset someone sends you into that folder
and it appears in the list. One that predates placement being part of a style says
nothing about where the lyrics go, so loading it leaves them where they are rather
than asserting a default it never chose.

## Settings storage

Changes apply and persist immediately — there is no OK or Apply button, matching
Windows 11 Settings, and the widget is on the taskbar the whole time so every change is
already previewed where it counts.

The file is `settings.json` in the data directory below. A missing or malformed file
falls back to defaults rather than failing to start, so it is safe to delete or
hand-edit, and it is written via a temp file and a swap so a crash mid-write cannot
truncate it. A file written by an older build is folded forward on load and rewritten
at once, so the fold happens once rather than on every launch until something happens
to change. A setting naming an option a newer build no longer knows costs that one
setting rather than resetting the whole file.

## Where data lives

Two roots, because the two builds are uninstalled differently.

Unpackaged, everything sits under `%LocalAppData%\Barline`. Removing that build means
deleting a folder, so anything left behind was always the user's to clear up.

Packaged, the root is the package's own local folder, which Windows deletes on
uninstall. That is the contract a packaged app is expected to honor, and writing to
`%LocalAppData%\Barline` from a package would leave a folder behind that nothing owns
and nothing removes. MSIX offers a redirection shim that would achieve the same
without a code change, but it works by letting the app believe it is writing
somewhere it is not, which is exactly the indirection that later produces "I put a
preset in the folder and it cannot see it".

The packaged path is longer and not memorable, so the settings window opens these
folders rather than printing them. That is also what makes the location an
implementation detail rather than something documented in two places that can drift.

The two builds therefore do not share state: installing the packaged build alongside
the portable one starts from defaults, the same way any two separate installations
would.

### Fetched lyrics are kept apart from imported ones

Under either root, `lyrics\` holds the `.lrc` files the user put there and `cache\`
holds what LRCLIB returned. They shared a folder at first, which was wrong in both
directions: the folder people are sent to when adding a file by hand was mostly
machine-named JSON, and the only thing keeping **Clear cache** from deleting
somebody's hand-corrected timings was a filename filter. A cache is disposable by
definition and an imported file cannot be re-fetched, so the difference belongs in
the layout rather than in a condition every future caller has to remember.

Splitting them moved the cache and left imports alone, so an existing `.lrc` keeps
working with nothing to do. Entries still in the old folder are moved on the next
launch; one already at the destination is dropped rather than overwritten, since both
copies are fetched results and neither is worth more than the other.

## The first launch

The widget hides itself when nothing is playing, which is right for every launch
except the first. Someone who has just installed this and has no music open sees no
change to their taskbar at all, and the only available conclusion is that it did not
work. That is the failure the welcome window exists to prevent, and it is worth more
than the setup instructions it also carries.

It is shown when there was **no settings file** at startup, rather than from a flag
inside the settings. A flag cannot tell a fresh install from an upgrade: a file
written by an older build has no such flag either way, so every existing user would
be greeted as though they were new. The file is written out immediately on a first
run for the same reason from the other direction, so somebody who changes no settings
is not greeted twice.

The sample in it is the real `Visualizer` control over the real backdrop estimate,
animating, rather than a screenshot. A picture would be wrong on a light theme, wrong
at a different accent, and stale the first time the design moved.

The advice about the Widgets button is shown only when `TaskbarDa` says the button is
actually there. Most of the setup advice an app can give is advice the reader has
already followed, and being told to undo something you never did is how a first run
starts to feel like a lecture. An unreadable or absent value counts as *visible*: the
cost is asymmetric, since an unneeded hint is read once and ignored, where a silent
overlap looks like the app is broken.

## About, and why it is not decoration

The `LICENSE` and `THIRD-PARTY-NOTICES.md` files are copied beside the executable by
the build, because a binary is conveyed under the GPL only if it carries the license
and says where its Corresponding Source can be had, and NAudio is MIT and compiled
*into* the binary rather than sitting next to it, so its notice reaches nobody unless
it is carried deliberately.

Shipping them is necessary but not sufficient for the Store build. A packaged app
installs under `WindowsApps`, and while a file in there reads perfectly well by full
path, the folder's root refuses to be listed — so Explorer cannot reach it and no
user will ever find a file inside it. The About card is the route: it opens both
documents, links to the repository, and states the version and which build is
running.

Those two files are opened in a window of the app's own rather than handed to the
shell. Neither can be opened that way: `LICENSE` has no extension, and `.md` has no
handler on a stock Windows install, so both would raise "How do you want to open this
file?", which reads as a fault rather than a document.

Version, repository and privacy URLs live together in `AppInfo` because they are
stated in more than one place — the LRCLIB user agent carries the version and a link
to the project, and the About card shows both. Both of those had already gone wrong
once while they were written out separately.

## Starting with Windows

Two mechanisms, because the same binary can run either way. Unpackaged, it writes the
per-user `Run` key — no elevation, and the app owns the setting outright.

Packaged, that key is not available: Windows ignores `Run` entries written by a
packaged app, so the same code would appear to succeed and silently do nothing. The
supported route is a startup task declared in the package manifest, which the user
approves and can revoke. Windows also lets the user switch a startup task off from
Task Manager and will refuse to let the app switch it back on, so the toggle reports
what Windows actually did rather than what was asked, and says why when they differ.
