# Privacy policy

**Barline**. Last updated 23 August 2026.

Barline collects nothing. The app contains no analytics, no telemetry, no crash
reporting, no account, and no advertising. Nothing you do in the app is reported to its
author, and the app never sees your identity.

The app makes two kinds of request, both described below: looking up lyrics, and, in
the Microsoft Store build, asking Windows about your license and about app updates.
Everything else it needs stays on your machine.

Installing from the Microsoft Store adds reporting that Barline does not perform and
cannot switch off. It is described under [The Microsoft Store
build](#the-microsoft-store-build).

## What leaves your computer

**Only when you turn lyrics on.** The lyrics feature is off by default and does
nothing until you switch it on under **Settings → Lyrics → Show lyrics**.

While it is on, looking up the lyrics for a track sends the following to
[LRCLIB](https://lrclib.net), a free public lyrics database:

- the track title
- the artist name
- the album name, when the source app reports one
- the track length in seconds

Nothing else is sent. No identifier, no account, no device information, and no record
of when or how often you listen. LRCLIB requires no key and no sign-in. The request
identifies the application and its version, in the form `Barline/2.0.0`, so the
service can recognize a misbehaving client; it does not identify you.

Results are cached on your machine, including the misses, so a track is looked up
once rather than once per play. Your network connection exposes the request to
LRCLIB and to anyone able to observe your traffic, as with any web request. LRCLIB's
own handling of requests is governed by its operators, not by this policy.

**If lyrics are off, Barline makes no lyrics requests at all.**

### Asking the Store

**Only in the Microsoft Store build.** That build asks Windows two questions through
the Store APIs built into the system: whether this machine owns the paid add-on, and
whether a newer version of Barline is waiting to be installed. The license question is
asked at startup; the update question a minute after startup and once a day after that.

These are calls to Windows, which answers them from the Store. Barline sends nothing of
its own with them: no identifier it invented, nothing about what you are playing, and
no record of how you use the app. Whatever your device tells Microsoft in the course of
answering is Microsoft's, under the [Microsoft Privacy
Statement](https://privacy.microsoft.com/privacystatement), and is the same exchange
that installing the app from the Store already involves.

**A build from source asks neither question.** There is no Store to ask, and it is
updated by replacing it.

## What is stored on your computer

In Barline's own data folder:

- `settings.json`: your preferences, and the app version that ran last, which is how
  the run after an update knows to say so.
- `presets\`: saved appearance presets.
- `lyrics\`: any `.lrc` files you import yourself.
- `cache\`: lyrics fetched from LRCLIB, including the misses.

Where that folder is depends on how you installed Barline. The portable build
keeps it at `%LocalAppData%\Barline`. The Microsoft Store build keeps it inside its
own package folder, which Windows deletes when you uninstall the app. Either way,
**Settings → About → Open folder** takes you there.

Under `%TEMP%`, only if you set `BARLINE_DEBUG=1` yourself:

- `barline.log`: a diagnostic log recording taskbar state, media sessions and
  playback timing. It is off unless you deliberately enable it.

All of this stays on your machine. You can delete any of it at any time; the app
falls back to defaults. Cached lyrics can be cleared from **Settings → Lyrics**.

## What the app reads locally

Barline reads what is currently playing through the Windows System Media Transport
Controls, the same source as the volume flyout, which gives it the title, artist,
album art and playback position. It also captures system audio output to drive the
visualizer. **Audio is analyzed for loudness in the moment and never recorded,
stored, or transmitted.** None of this leaves your computer except as described above.

## The Microsoft Store build

Everything above describes what Barline does, and it is the same in every build. The
Store adds something on top that the app neither performs nor controls.

Windows and the Microsoft Store report figures about Store apps back to their
developer, through the Partner Center dashboard. For Barline that means install and
acquisition counts, active device counts, session counts and engagement time, crash
and hang reports, and any rating or review you choose to leave.

Three things are worth being precise about:

- **Barline does not do this.** No code in the app gathers or sends any of it. It is
  collected by Windows and the Store as part of distributing an app through them.
- **It is aggregate.** The developer sees counts, and can break them down by things
  like app version, region and Windows build. It does not name you, and there is no way
  to look up an individual person or their listening.
- **It is not governed by this policy.** That collection is Microsoft's, under the
  [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement). What
  your device sends to Microsoft is controlled by your Windows diagnostic data
  settings, under **Settings → Privacy & security → Diagnostics & feedback**, not by
  anything in Barline.

**A build from source is not distributed through the Store and reports none of this.**

## Children

Barline is not directed at children and collects no personal information from anyone.

## Changes

Any change to this policy will appear in this file, and its history is public in the
[repository](https://github.com/madeitdisfar/Barline).

## Contact

Open an issue at <https://github.com/madeitdisfar/Barline/issues>.
