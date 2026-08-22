# Design notes

Why Barline works the way it does. This is the reasoning behind the decisions: the
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
- **It is a satellite of one taskbar window**, mirroring that window's rect, DPI,
  visibility and z-order. One mechanism covers auto-hide, fullscreen apps, DPI changes
  and Explorer restarts. Auto-hide needs no special handling at all: the taskbar
  slides off-screen and the widget slides with it.

Hovering crossfades the visualizer into previous / play-pause / next inside a
fixed-width zone, so nothing reflows. Layout shift on hover is the fastest way to
look third-party.

### Which taskbar

Windows draws a taskbar on every display only if asked to, so on a default machine
there is one, on the primary, and the widget is on it. Asked to, Explorer adds a
`Shell_SecondaryTrayWnd` per display, and the widget has to be told which one it is a
satellite of.

That is a **choice of one taskbar**, not a widget on each. Being the satellite of a
single window is what the whole placement rests on: one rect, one DPI, one owner for
the z-order, one monitor for the lyrics panel to anchor to. A widget per taskbar
would multiply all four and then raise a question with no good answer, since three
floating lyric panels saying the same line is plainly not what anyone wants. Keeping
the invariant costs a selection rule, and the enumeration it needs is most of what a
widget-per-taskbar version would need later anyway.

The stored choice is the monitor's **device path**, from the DisplayConfig API, not
`\\.\DISPLAY2` and not "display 2". Those are slots: reconnect two screens the other
way round and they swap under a setting nobody touched. The device path is built from
the monitor's EDID and survives replugging, a different port, and renumbering.

The rule that matters is the fallback. A display that is not connected is a normal
condition, not a broken setting, so the choice is never rewritten. Acquisition drops
to the primary taskbar and takes the choice up again when the monitor returns, which
is what a docked laptop does twice a day. A widget that vanished instead would read
as a crash, and the taskbar it was riding vanished with it, so there is nowhere else
for it to have gone.

`WM_DISPLAYCHANGE` re-acquires rather than only re-placing, since the taskbar handle
survives a monitor arriving and nothing else would notice the chosen display coming
back. It also retries for a few seconds afterwards: Explorer creates the new
taskbar some time after the event that announced the monitor, so a single attempt
looks at a desktop that does not have it yet.

### Which end

The widget takes the left end of the taskbar, which is empty on the centered taskbar
Windows 11 ships with. Set the taskbar to the left instead and that end is Start, so
the widget crosses to the far end and parks against the notification area, the only
stretch a left-aligned taskbar leaves free.

Finding that stretch means finding where the tray begins. Windows 11 draws its taskbar
in XAML, but Explorer still keeps the old child windows and still moves them:
`TrayNotifyWnd` was measured landing within a pixel of where UI Automation puts the
first tray button, and within a pixel of where the chevron is actually drawn. One
`GetWindowRect` is enough, with no UI Automation and nothing to keep in step.

The task buttons are *not* knowable that way, which is worth knowing before leaning on
any of these windows. `MSTaskSwWClass` reported 1132..1660 while the buttons really ran
1043..1837, missing Start, Search, Task View and the last two apps. Only the XAML tree
has that truth. So the widget is placed against the one edge the legacy windows still
get right, and never against the buttons.

Which end to take is read from `TaskbarAl`, the setting itself, since there is no
window to measure for it and no shell API that reports it. It is read on the same
one-second reconcile as everything else, so switching alignment moves the widget
without a registry watcher, and an absent or unexpected value counts as centered:
every existing user has the widget at the left end, and it should take Windows saying
plainly otherwise to move it.

Moves along the taskbar are animated, over the same quarter second and the same Fluent
curve as everything else in the widget: arriving somewhere else instantly reads as a
glitch rather than as a move, and gives no sense that it is the same widget. Only moves
along a taskbar that has not otherwise changed are eased, which is exactly the two
things that move the widget while somebody is watching, the tray changing width and the
alignment changing. The widget is a satellite, and one that eased into position would
trail its taskbar through an auto-hide, a resolution change or a jump to another
display. What is animated is the window rather than what is drawn in it: the widget
paints to its edges, so a render transform would slide the content out of a frame that
stayed where it was.

The lyrics panel's widget position crosses with it, and hangs from the same end of the
screen rather than from the widget's own edge. The panel is narrower than the widget,
so lining the two up would leave it floating short of the corner at one end and past
it at the other. What it belongs to is the widget; what it is measured from is the
screen, less the margin that keeps a rounded corner out of the corner of the display.

Two things this does not solve. Left-aligned buttons grow rightward as windows open,
and Windows only starts overflowing them when they fill the whole taskbar, which it
measures without knowing the widget is there, so enough windows will still reach it.
And a right-to-left Windows mirrors the taskbar; the clamp keeps the widget on the
taskbar there, but the placement has not been tested on one.

### The tray menu is WPF

The notification area's menu was a WinForms `ContextMenuStrip` for as long as the tray
icon needed `NotifyIcon`, which WPF has no answer for. Keeping the two together looked
economical and was not. The drop-down took its scale from a field the framework updates
only after its window has moved, so a menu opening on a second display was laid out for
the one it was last on. It rescaled any font assigned to it by the ratio between its
window's creation DPI and the current one, so a font sized for the target was scaled
toward it twice. It sized items wider than the menu holding them, and clipped the
longest label. It computed its image gutter once, at creation, and scaled it again per
display, which no public property could reach.

Each of those had a workaround, and together they were about 250 lines of GDI painting
whose entire purpose was to make a 2005 control look like Windows 11. The menu is now a
WPF `ContextMenu` with a styled template. Nothing in it multiplies by a DPI scale: the
app declares PerMonitorV2 and WPF honors it, so the sizes are logical units and a
display at another scale is not a case to compensate for. Measured, the flyout is the
same 143.5x162 logical on a 200% and a 150% display. It also reads the theme's brushes
directly rather than flattening their alpha by hand, GDI having no compositing.

Two things had to be arranged. The flyout hangs from a one-pixel invisible window moved
to the pointer first: a `ContextMenu` with no placement target has no visual parent, and
WPF hands its popup the primary display's scale wherever it actually appears, which
reproduced the original complaint exactly. That window doubles as the activation Win32
requires, since a popup owned by nothing never gets the message that lets it close when
the next click lands elsewhere. And the menu is built fresh for each opening, because a
WPF popup parks its window at the origin on close and recomputes its position only when
something it watches has changed, so a second right-click without moving the mouse
reopened it in the corner of the screen.

The flyout opens upward from the pointer, which is what a notification-area menu does,
and is anchored clear of the taskbar rather than on it. Both ways
of opening it put the pointer on the taskbar, and WPF fits a flyout to the screen rather
than to the working area, so the last item came to rest under the widget: covered,
because the widget is topmost, and dead to clicks, because the widget takes input.
Clamping to the working area handles that, and handles a taskbar docked to any edge. A
taskbar set to hide itself reserves no working area, though, so a second pass pushes the
anchor off the taskbar's own rectangle, across it rather than along it and inward rather
than toward the nearest edge. The nearest edge of a taskbar along the bottom is the
bottom of the screen.

That cleared the widget but not the lyrics panel, which sits directly above it. Both are
topmost and the flyout was losing to them: the widget re-asserts `HWND_TOPMOST` every
400ms so Show Desktop cannot leave it uncomposited, and one tick after the flyout opened
it was back on top of it. The panel rode up as well, because a `SetWindowPos` that raises
an owned window carries its owner with it and everything Barline puts on the taskbar is
owned by `Shell_TrayWnd`. Measured on a bottom taskbar: the flyout was first in the
topmost band the instant it appeared, and third 400ms later. The timer now asks whether
the app's own menu is up and skips the tick while it is. A question asked each tick
rather than a timer stopped and started, so nothing has to be turned back on: however the
flyout ends, the next tick asks again and the widget resumes by itself.

One more thing follows from building the menu per opening. Right-clicking the tray while
the flyout is up dismisses it and opens another, and the dismissal finishes after the
replacement is already hanging from the anchor, so the outgoing menu's close handler
would hide the anchor out from under its successor and the new menu would vanish a
tenth of a second after appearing. The handler checks that it is still the menu on
screen before tidying anything away.

## Media

**Metadata comes from SMTC** (`GlobalSystemMediaTransportControlsSessionManager`), the
same source the Windows volume flyout reads, so Spotify, Apple Music, browsers and
podcast apps work without per-app integration.

**Playback position is extrapolated, not read.** SMTC does not tick: a source app
publishes a position when it feels like it: measured against Spotify, roughly every
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
band's level is the RMS *per bin*, a spectral density rather than a total, so
slicing the span more finely does not systematically lower it.

**The capture self-heals.** A loopback capture stays bound to one device and doesn't
follow the default as it moves, and after idle or sleep it can silently stall without
notifying the app. Both used to need a restart. A watchdog re-arms it when it has
died, when the default output moves elsewhere (e.g. headphones reconnect after sleep),
or when it goes quiet while a track is still playing. Capture is also re-armed on
resume from sleep, and **Restart visualizer** in the menu forces it by hand.

### Why six bars is the ceiling

Bar counts divide a fixed width *and* a fixed amount of ink, so a higher count means
thinner bars rather than a wider or heavier visualizer: the widget never reflows the
taskbar layout, and the row keeps the visual weight its color was corrected for.

Six is the ceiling for two independent reasons. Seven bars would be 1.7px wide, and
neighboring tall bars visibly merge at 100% display scaling, worse on the light
taskbar, where the bar color is translucent as well as thin. Seven bands would also
outrun the transform: a 1024-point FFT at 48 kHz resolves 46.9 Hz per bin, and the
lowest band's 40–89.8 Hz span falls inside the single bin its neighbor starts from,
so the bottom two bars would carry identical data and move as one.

## Color and legibility

Every bar color mode except `Default` is **corrected for legibility** before it is
drawn. The hue is the artwork's (or yours) to choose; the lightness is not. A dark
navy cover on the dark taskbar or a pale yellow one on the light taskbar would
otherwise paint bars that are technically colored and practically invisible, so the
hue is kept and the lightness is pushed until the bars clear a 3:1 contrast ratio,
WCAG's threshold for non-text graphics, against the taskbar. Saturation is held
inside a band for the same reason, so a correction can neither bleach the hue to gray
nor push it into neon. A cover with no usable hue at all, like a black-and-white
sleeve, falls back to `Default` rather than inventing a tint.

Because of that, the settings window shows each mode's **resolved** color and hex,
what will actually be drawn rather than what was picked, over a strip painted the same shade
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
nothing filed today is one nobody has got to yet, and a newly released song is
exactly the one most likely to be filled in shortly after you first ask for it. So a
miss is retried on a **widening delay**: the next day, then after three, seven and
thirty. That catches the common case quickly while stopping a library of instrumentals
from asking again on every play, and it means no track is ever written off for good.

### Matching, not coverage

Matching is the hard part. Spotify reports `Creep - Remastered 2011` and a browser
reports `Creep (Official Video)`; neither string is filed under that name anywhere.
Lookups therefore widen in stages: the name exactly as reported first, since
stripping is a guess and a track really can be called `Live`, then with packaging
removed, then with only the first credited artist, and finally a duration-matched
search that tolerates a source reporting the length a second or two out.

### Where a line ends

Where a record has one, LRCLIB's own **lyricsfile** form is preferred over the LRC. It
is the same lyrics as YAML, and it states each line's *end*, which LRC cannot
express, leaving a line to implicitly run until the next one starts. That is wrong
across every instrumental gap, and it is precisely the span the word timing divides
up. The format is documented as supporting word-level timing as well, but no
contributed data appears to use it yet: a sample of roughly a hundred records carried
only line-level fields, so nothing here guesses at a schema that isn't in the data.

### Word by word

The panel highlights either **each word as it is sung** or the whole line at once,
whichever you prefer. Word timing is estimated for almost every track, and on one it
fits badly a line at a time is calmer.

No free source carries word-level timing, so it is inferred: the line's span is
divided by **syllable count**, which tracks singing time far better than character
count: *strength* and *a potato* are the same length and nothing alike to sing.
Vowel-group counting is an English heuristic, so scripts that write a syllable per
character (Hangul, kana, CJK) are counted that way instead; otherwise a Korean line
would come back as one syllable per word and the sweep would be worthless. A file that
carries real word timings always overrides the estimate.

Aligning the words to the audio properly was considered and rejected, not because it
is slow but because it is **causal**. A forced aligner cannot know where a word lands
until after it has been sung, so using one would mean running the lyrics behind the
music, which defeats the point at any speed.

### The panel

The panel is a genuinely transparent window: WPF gives it real per-pixel alpha, and
the background is painted over whatever is behind it at the opacity you choose.

It never takes input: clicks pass straight through to whatever is behind, and it is
owned by the taskbar, so it hides for fullscreen apps and slides away with auto-hide
by the same mechanism the widget does. Because it cannot be clicked away, it can
instead **fade or hide while the pointer is over it**. The pointer is polled rather
than handled as an event, since a click-through window never receives one.

It also **waits a few seconds before disappearing**. Between tracks there is a moment
with no lyrics (the old ones dropped, the new ones not yet fetched), and hiding
immediately made the panel blink out and back on every song change. Showing is never
delayed; only hiding. A source app closing is a different thing from a gap between
songs, so the panel goes when the widget does rather than waiting out that grace.

Position anchors are measured from the taskbar's own monitor, so the panel follows the
taskbar rather than assuming the primary screen. **Free** stores its position as a
share of the screen rather than in pixels, so it stays put when the resolution
changes, and is adjustable a tenth of a percent at a time, because one percent is 26 physical
pixels on a wide screen, which is too coarse to line the panel up with anything.

In the widget, instrumental gaps are timed too, and during one the title returns
rather than the previous line hanging around. So does pausing: a lyric sitting there
over music nobody is playing reads as stuck, and what you want to know about a paused
track is what it is. Turning the visualizer off gives that reserved width back to the
text: 150px becomes 238px, over half as much again, since the zone beside it is then
drawing nothing.

## The style is the setting

Font family, size, weight and italic, text color, unsung-word opacity, casing,
effect, background and corner radius are all settings, **and so are where the lyrics
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
happens to save. Those are different questions, and answering the second with the
first is what once put the display mode inside the style card while the settings that
react to it sat outside. Controls that a choice makes meaningless are hidden rather
than left to do nothing: an effect radius with no effect, an opacity for an opaque
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
worth what it bought. Every background is now painted by the app, and the corner
radius applies to all of them alike.

### Presets are files

Presets live in the `presets` folder under Barline's data directory (see
[Where data lives](#where-data-lives)). They are saved copies of the style,
not a separate source of truth: the settings window edits it directly and you see it
immediately, and a preset is that snapshot under a name. The alternative, the file
being the only home for these values, would mean every tweak was a file edit and the
UI could only pick between whole files.

Seven looks ship as ordinary preset files: `Widget`, `Widget_Glow` and `Widget_Movie`
for the inline display, and `Clean`, `Glow`, `Movie` and `Raw` for the panel. Which of
them are written depends on the license, since four of the seven glow: see
[The paid features](#the-paid-features). They are written on first run and never
overwritten afterwards, so an edited built-in survives an update. That makes them
readable, copyable and editable, and means writing your own starts from a working
example rather than an empty file. Drop a preset someone sends you into that folder
and it appears in the list.

Withdrawing a built-in has to reach into the user's folder, so it happens only while
the file still matches the copy we shipped, field for field. An edited one is theirs
and stays under its old name. `Lime` was retired that way when `Raw` replaced it. One that predates placement being part of a style says
nothing about where the lyrics go, so loading it leaves them where they are rather
than asserting a default it never chose.

## Settings storage

Changes apply and persist immediately. There is no OK or Apply button, matching
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

## Show Desktop

Show Desktop used to take the widget away and keep it away, until something unrelated
moved the taskbar and brought it back, which is why clicking the taskbar looked like
the cure.

It is worth writing down what it is *not*, because every plausible explanation turns
out to be wrong. Measured while Show Desktop was active and confirmed engaged (the
foreground window really was `Progman`): `WS_VISIBLE` still set, `IsIconic` false,
`WS_EX_TOPMOST` still set, DWM reporting the window uncloaked, and its position in the
z-order unchanged and still above `Shell_TrayWnd`. Nothing about the window changes at
all. Windows simply stops compositing it, because the shell's own windows are the only
ones exempt from that state.

Since nothing changes, nothing is a state change, so the tracker has nothing to report
and the widget stays gone. The fix is not to detect it, since there is no signal to detect,
but to stop relying on being told. A bare `SetWindowPos` restores the widget
immediately, even while the desktop is still showing, so the overlay re-asserts its
z-order every 400ms whenever it should be visible.

Only the showing path re-asserts. The hide path is debounced precisely so a transient
state cannot blink the widget, and a timer that could reach it would re-arm that
debounce forever.

It is polling, so the price was measured rather than waved at. A *full* placement, the
shape this used at first, costs about **0.9ms**, because a layered window with
per-pixel alpha goes through composition on every call; driving it at 61 calls a second
cost 5.1 to 5.6 percentage points of one core across two runs. At 2.5 calls a second
that puts a ceiling of roughly **0.2% of one core** on the re-assert, and the call it
actually issues does strictly less work than the one that was measured.

A ceiling rather than a figure, because the difference cannot be measured here. Three
baseline samples of the same build came in at 23.1%, 20.2% and 29.9% of one core, so
run-to-run noise swamps a tenth of a point several times over. For scale, that baseline
is the visualizer animating while a track plays; with nothing playing the widget is
hidden, the guard skips the call entirely, and the process sits at 0.7% of one core.

### Ask for the z-order and nothing else

The re-assert first went through the normal placement, which moves, resizes and
re-inserts the window into the topmost band. At 400ms that flickered whenever a taskbar
item was clicked. Clicking one raises `Shell_TrayWnd` and hands focus elsewhere, and
the widget rides up with the taskbar as its owned window, so a full reposition landing
inside that reorder showed.

Slowing the timer to a second hid it, and that was the wrong fix: the interval decides
how often the collision is possible, not whether it is. The cause was the tick doing
far more work than the job asks for. Recovering from Show Desktop was measured to need
only a `SetWindowPos` carrying `SWP_NOMOVE | SWP_NOSIZE`: put the window back in the
composition, change nothing else. On a layered window the difference is a whole
composition pass against an empty one.

Issuing the minimal call removed the flicker at 400ms, confirmed by hand, which is the
only way it could be confirmed: it never appeared to any measurement, only to somebody
clicking around a real taskbar. So the interval stays at 400ms and Show Desktop no
longer leaves a visible gap.

Geometry is deliberately not this timer's business. It arrives through the tracker,
which is the path that owns it and the only one that knows when it changed.

Reparenting the widget to `Shell_TrayWnd` would inherit the shell's exemption outright
and was tried first. It was abandoned: a child's coordinates are relative to its
parent, and the DPI handling that follows from that put the window at twice its
intended offset. Re-asserting achieves the same result without touching the
positioning model.

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
actually there. It does not depend on which end the widget is at: aligning the taskbar
left moves Windows' own Widgets button to the far end too, so the two collide there
just as they do at the left end of a centered one. Most of the setup advice an app can give is advice the reader has
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
path, the folder's root refuses to be listed, so Explorer cannot reach it and no
user will ever find a file inside it. The About card is the route: it opens both
documents, links to the repository, and states the version and which build is
running.

Those two files are opened in a window of the app's own rather than handed to the
shell. Neither can be opened that way: `LICENSE` has no extension, and `.md` has no
handler on a stock Windows install, so both would raise "How do you want to open this
file?", which reads as a fault rather than a document.

Version, repository and privacy URLs live together in `AppInfo` because they are
stated in more than one place: the LRCLIB user agent carries the version and a link
to the project, and the About card shows both. Both of those had already gone wrong
once while they were written out separately.

## The paid features

Five of them: the Balanced and Detailed bar counts, the album art bar color, the
freely placed lyrics panel, the glow effect, and saving or importing your own preset.
Everything else stays free, and the portable build hands all five over for nothing.
It is built from GPL-3.0 source, and gating it would only inconvenience the people the
license is written for. What the Store sells is the packaged app: updates, a sandboxed
install, and not compiling anything.

That shapes the gate. It is one plain bool behind one plain check, with no
obfuscation, because anyone who wants it gone can legally compile it out. Effort spent
hiding it would only make the code worse for the people who paid.

### Three states, not a bool

`LicenseService` answers with `Licensed`, `NotLicensed` or `Unknown`, and the third is
the whole point. "No" and "could not ask" disable the same controls but mean opposite
things about the user's file: a real no is grounds for taking paid values out of it,
and a failed question never is. Two booleans come off the state. `Premium` asks
whether a feature is available and `MayStrip` whether the file may be edited, and only a
positive no sets the second.

`Unknown` is not mainly a licensed user's state. It is reached by anyone we have never
confirmed while the Store cannot be asked, which includes every free user during a
fault and, more importantly, an owner on a fresh install or a new machine. The
remembered yes lives in the app data root, which Windows deletes on uninstall and
wipes on Reset, so a genuine owner does arrive with no memory. Collapsing `Unknown`
into `NotLicensed` would strip that person's settings on first launch.

A yes is remembered with a timestamp and honored for a year if the Store later goes
quiet. Expiring it sooner cannot guard against refunds. A refund comes back as a
positive no the moment the Store *can* be asked, and that deletes the memory outright,
so a short window would only ever cost a paying customer.

Nothing checks the license at render time. The visualizer draws whatever bar count it
is handed and the panel draws whatever effect it is handed. Enforcement is at two
doors only: the control that would choose it, and the load that would apply it. That
is why `Unknown` leaves an existing glow working while locking the control that sets
it, and why the locked control says which of the two states it is in. Telling someone
who paid that they have not bought this would be a lie to exactly the wrong person.

### Stripping, and why it is safe to be wrong

Paid values are removed from `settings.json` rather than ignored in it. The file is
documented as hand-editable and the About card has a button that opens the folder it
sits in, so leaving the values live would put the gate one text edit away by a route
the app itself points at. "Live but pretend you cannot see it" is a worse contract
than "removed".

Everything removed is written to `premium-backup.json` first, and only a successful
restore deletes it. So the worst a mistaken strip can do is move values into a sidecar
for one run. A restore puts a value back only if the setting is still sitting on the
fallback that replaced it, so a choice made while unlicensed is newer than the backup
and wins.

### Presets

Two paid features can appear inside a style, the glow and the free position, so
`UsesPremium` asks the content rather than the name. Built-ins and saved presets are
the same kind of file in the same folder, and a file written while licensed outlives
the license, so anything keyed off the name would let a rename carry a paid look into
a free build.

Three places act on that, and all three are needed. Seeding skips paid built-ins
entirely, so a free install's folder never holds a look the build cannot draw. Listing
is an allowlist of the free built-in names, not a content filter, because filtering on
content alone would still hand a free build any preset copied into the folder, and
keeping your own presets is itself what is being sold. Loading asks again, because the
listing goes by name and the contents of a file under a free name are not ours to
trust; that is the only place a style actually becomes the live one.

Four of the seven built-ins glow, which leaves Widget, Widget_Movie and Clean free.
That floor is why the glow was gated rather than the background or the text color:
either of those would have taken the remaining three down with it.

Retiring a built-in deletes a file out of the user's folder, so it happens only when
every visual field still matches the copy we shipped. An edited one is theirs.

## Starting with Windows

Two mechanisms, because the same binary can run either way. Unpackaged, it writes the
per-user `Run` key: no elevation, and the app owns the setting outright.

Packaged, that key is not available: Windows ignores `Run` entries written by a
packaged app, so the same code would appear to succeed and silently do nothing. The
supported route is a startup task declared in the package manifest, which the user
approves and can revoke. Windows also lets the user switch a startup task off from
Task Manager and will refuse to let the app switch it back on, so the toggle reports
what Windows actually did rather than what was asked, and says why when they differ.
