using System.IO;
using System.Windows.Threading;
using Barline.Diagnostics;
using Barline.Media;
using Barline.Platform;
using Barline.Settings;

namespace Barline.Lyrics;

/// <summary>
/// Supplies lyrics for whatever is playing.
/// </summary>
/// <remarks>
/// <para>
/// Off unless the user turns it on. Looking a track up means sending its title and
/// artist to a third party, which is not something a taskbar widget should start
/// doing on its own.
/// </para>
/// <para>
/// A user-supplied file always wins over the network. Someone who has gone to the
/// trouble of placing an <c>.lrc</c> beside their music, or of correcting a bad set
/// of timings, means it — and it is also the only route for tracks the database has
/// never heard of.
/// </para>
/// </remarks>
internal sealed class LyricsService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly LyricsCache _cache = new();
    private readonly LrcLibClient _client = new();
    private readonly Dispatcher _dispatcher;

    /// <summary>Where hand-supplied files are looked for and imports are written.</summary>
    private readonly string _imports = AppPaths.Lyrics;

    /// <summary>Cancels the in-flight lookup when the track changes under it.</summary>
    private CancellationTokenSource? _inFlight;

    /// <summary>What the current document was fetched for, to avoid refetching it.</summary>
    private string? _currentKey;

    /// <summary>The track and length last asked for, so an import can reload them.</summary>
    private TrackInfo? _currentTrack;
    private TimeSpan _currentDuration;

    private bool _disposed;

    /// <summary>
    /// Lyrics for the current track. Empty until a lookup completes, and empty for
    /// good when the track has none.
    /// </summary>
    public LyricsDocument Current { get; private set; } = LyricsDocument.Empty;

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    public LyricsService(SettingsStore settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;

        // Written on first run so the built-in looks are ordinary, readable files
        // rather than something compiled in and hidden.
        new LyricsPresetStore().EnsureBuiltIns();
    }

    /// <summary>The folder to open for someone adding a file by hand.</summary>
    public string ImportsDirectory => _imports;

    /// <summary>How many tracks are cached, and how much room they take.</summary>
    public (int Count, long Bytes) MeasureCache() => _cache.Measure();

    /// <summary>
    /// Throws away every fetched result. Imported files are left alone.
    /// </summary>
    /// <remarks>
    /// The document on screen is dropped too, so the current track is looked up again
    /// rather than carrying on from an entry that no longer exists.
    /// </remarks>
    public int ClearCache()
    {
        int removed = _cache.Clear();

        var track = _currentTrack;
        var duration = _currentDuration;

        _currentKey = null;
        if (track is not null) SetTrack(track, duration);

        return removed;
    }

    /// <summary>
    /// Points the service at a track. Safe to call repeatedly — a track already
    /// loaded, or already being loaded, costs nothing.
    /// </summary>
    public void SetTrack(TrackInfo? track, TimeSpan duration)
    {
        if (_disposed) return;

        if (!_settings.Current.LyricsEnabled || track is null || !track.HasContent)
        {
            Clear();
            return;
        }

        // Without a length there is no way to tell one recording of a track from
        // another, and the lookup would be a guess dressed up as a match.
        if (duration <= TimeSpan.Zero)
        {
            DebugLog.Write("lyrics: no duration reported; skipping lookup");
            Clear();
            return;
        }

        string key = LyricsCache.KeyFor(track.Title, track.Artist, duration);
        if (key == _currentKey) return;

        Clear();

        _currentKey = key;
        _currentTrack = track;
        _currentDuration = duration;

        _inFlight = new CancellationTokenSource();
        _ = LoadAsync(track, duration, key, _inFlight.Token);
    }

    private async Task LoadAsync(
        TrackInfo track,
        TimeSpan duration,
        string key,
        CancellationToken cancellation)
    {
        try
        {
            var document = await Task
                .Run(() => ResolveAsync(track, duration, key, cancellation), cancellation)
                .ConfigureAwait(false);

            if (cancellation.IsCancellationRequested) return;

            // Publish only if this is still the track being asked about; a slow
            // lookup must not overwrite the one that replaced it.
            _ = _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || key != _currentKey) return;

                Current = document;
                DebugLog.Write(
                    $"lyrics: {(document.IsEmpty ? "none" : $"{document.Lines.Count} lines")} " +
                    $"synced={document.IsSynced} for '{track.Title}'");

                Changed?.Invoke(this, EventArgs.Empty);
            }));
        }
        catch (OperationCanceledException)
        {
            // The track moved on. Nothing to report.
        }
        catch (Exception ex)
        {
            DebugLog.Write($"lyrics: load failed: {ex.Message}");
        }
    }

    private async Task<LyricsDocument> ResolveAsync(
        TrackInfo track,
        TimeSpan duration,
        string key,
        CancellationToken cancellation)
    {
        // A file the user placed themselves outranks anything fetched.
        var imported = ReadImported(track);
        if (imported is not null) return imported;

        var stored = _cache.Read(key);

        if (stored is not null && LyricsCache.IsUsable(stored, DateTimeOffset.UtcNow))
        {
            DebugLog.Write($"lyrics: cache {(stored.Found ? "hit" : "known miss")} for '{track.Title}'");
            return ToDocument(stored);
        }

        var record = await _client
            .FindAsync(track.Title, track.Artist, track.AlbumTitle, duration, cancellation)
            .ConfigureAwait(false);

        var entry = new CachedLyrics
        {
            Found = record is not null,

            // Carried forward across a retry, so each empty answer pushes the next
            // attempt further out. Reset by a hit, which ends the sequence anyway.
            Misses = record is null ? (stored?.Misses ?? 0) + 1 : 0,

            SyncedLyrics = record?.SyncedLyrics,
            PlainLyrics = record?.PlainLyrics,
            LyricsFile = record?.LyricsFile,
            FetchedUtc = DateTimeOffset.UtcNow,
            Schema = LyricsCache.CurrentSchema,
            TrackName = record?.TrackName ?? track.Title,
            ArtistName = record?.ArtistName ?? track.Artist,
        };

        _cache.Write(key, entry);

        if (record is null)
        {
            DebugLog.Write(
                $"lyrics: nothing filed for '{track.Title}' " +
                $"(miss {entry.Misses}; asking again in {LyricsCache.RetryAfter(entry.Misses).TotalDays:F0} days)");
        }

        return ToDocument(entry);
    }

    /// <summary>
    /// Looks for a hand-supplied file, named for the track, in the lyrics folder.
    /// </summary>
    /// <remarks>
    /// A folder of Barline's rather than beside the audio, because SMTC identifies a
    /// session by app id and never reveals a file path — for Spotify or a browser
    /// there is no local file to sit beside.
    /// </remarks>
    private LyricsDocument? ReadImported(TrackInfo track)
    {
        try
        {
            string name = Sanitize($"{track.Artist} - {track.Title}");
            string path = Path.Combine(_imports, $"{name}.lrc");

            if (!File.Exists(path)) return null;

            DebugLog.Write($"lyrics: using imported file {name}.lrc");
            return LrcParser.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"lyrics: imported file unreadable: {ex.Message}");
            return null;
        }
    }

    private static LyricsDocument ToDocument(CachedLyrics entry)
    {
        if (!entry.Found) return LyricsDocument.Empty;

        var synced = LrcParser.Parse(entry.SyncedLyrics);

        // Real word timings outrank everything. The lyricsfile form states where each
        // line ends, which is worth having — but only the enhanced LRC extension
        // carries per-word times, and an estimate bounded by a true line end is still
        // an estimate. Preferring lyricsfile unconditionally would throw away actual
        // data in favor of a better guess.
        if (synced.Lines.Any(line => line.Words is not null)) return synced;

        // Otherwise take the stated line ends: the LRC leaves them to be inferred from
        // the next line's start, which is wrong across every instrumental gap, and
        // that span is precisely what the word timing divides up.
        if (LyricsFileParser.Parse(entry.LyricsFile) is { } stated) return stated;

        // Timed lyrics if they exist, plain text if not. An instrumental has neither,
        // and correctly resolves to nothing to show.
        return synced.IsEmpty ? LrcParser.Parse(entry.PlainLyrics) : synced;
    }

    // ---- Importing ---------------------------------------------------------

    /// <summary>
    /// The name a hand-supplied file must have to be picked up for a track.
    /// </summary>
    public static string FileNameFor(TrackInfo track) =>
        $"{Sanitize($"{track.Artist} - {track.Title}")}.lrc";

    /// <summary>Full path an import for this track would occupy.</summary>
    public string ImportPathFor(TrackInfo track) =>
        Path.Combine(_imports, FileNameFor(track));

    /// <summary>Whether a hand-supplied file is already in place for a track.</summary>
    public bool HasImport(TrackInfo track) => File.Exists(ImportPathFor(track));

    /// <summary>
    /// Copies a chosen file into place for a track and reloads immediately.
    /// </summary>
    /// <remarks>
    /// The file is parsed before it is kept. Copying something unreadable in would
    /// silently replace working lyrics with nothing, and the failure would look like
    /// the track having none.
    /// </remarks>
    public bool TryImport(TrackInfo track, string sourcePath, out string message)
    {
        try
        {
            string text = File.ReadAllText(sourcePath);
            var parsed = LrcParser.Parse(text);

            if (parsed.IsEmpty)
            {
                message = "That file has no lyrics this can read.";
                return false;
            }

            Directory.CreateDirectory(_imports);
            File.WriteAllText(ImportPathFor(track), text);

            // Drop the loaded document so the next request picks the file up rather
            // than the cached network result.
            _currentKey = null;
            SetTrack(_currentTrack ?? track, _currentDuration);

            message = parsed.IsSynced
                ? $"Imported {parsed.Lines.Count} timed lines."
                : $"Imported {parsed.Lines.Count} lines, but the file has no timings.";

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"lyrics: import failed: {ex.Message}");
            message = $"Could not import: {ex.Message}";
            return false;
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name.Trim();
    }

    private void Clear()
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
        _currentKey = null;

        if (Current.IsEmpty) return;

        Current = LyricsDocument.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _client.Dispose();
    }
}
