# Privacy policy

**Barline** — last updated 6 August 2026.

Barline collects nothing. There is no analytics, no telemetry, no crash reporting, no
account, and no advertising. Nothing you do in the app is reported to its author, and
the app never sees your identity.

The app makes exactly one kind of network request, described below. Everything else
it needs stays on your machine.

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
service can recognise a misbehaving client; it does not identify you.

Results are cached on your machine, including the misses, so a track is looked up
once rather than once per play. Your network connection exposes the request to
LRCLIB and to anyone able to observe your traffic, as with any web request. LRCLIB's
own handling of requests is governed by its operators, not by this policy.

**If lyrics are off, Barline makes no network requests at all.**

## What is stored on your computer

In Barline's own data folder:

- `settings.json` — your preferences.
- `presets\` — saved appearance presets.
- `lyrics\` — any `.lrc` files you import yourself.
- `cache\` — lyrics fetched from LRCLIB, including the misses.

Where that folder is depends on how you installed Barline. The portable build
keeps it at `%LocalAppData%\Barline`. The Microsoft Store build keeps it inside its
own package folder, which Windows deletes when you uninstall the app. Either way,
**Settings → About → Open folder** takes you there.

Under `%TEMP%`, only if you set `BARLINE_DEBUG=1` yourself:

- `barline.log` — a diagnostic log recording taskbar state, media sessions and
  playback timing. It is off unless you deliberately enable it.

All of this stays on your machine. You can delete any of it at any time; the app
falls back to defaults. Cached lyrics can be cleared from **Settings → Lyrics**.

## What the app reads locally

Barline reads what is currently playing through the Windows System Media Transport
Controls — the same source as the volume flyout — which gives it the title, artist,
album art and playback position. It also captures system audio output to drive the
visualiser. **Audio is analysed for loudness in the moment and never recorded,
stored, or transmitted.** None of this leaves your computer except as described above.

## Children

Barline is not directed at children and collects no personal information from anyone.

## Changes

Any change to this policy will appear in this file, and its history is public in the
[repository](https://github.com/madeitdisfar/Barline).

## Contact

Open an issue at <https://github.com/madeitdisfar/Barline/issues>.
