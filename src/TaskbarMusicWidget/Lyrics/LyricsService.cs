using System.IO;
using System.Windows.Threading;
using TaskbarMusicWidget.Diagnostics;
using TaskbarMusicWidget.Media;
using TaskbarMusicWidget.Settings;

namespace TaskbarMusicWidget.Lyrics;

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

    /// <summary>Cancels the in-flight lookup when the track changes under it.</summary>
    private CancellationTokenSource? _inFlight;

    /// <summary>What the current document was fetched for, to avoid refetching it.</summary>
    private string? _currentKey;

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
    }

    public string CacheDirectory => _cache.DirectoryPath;

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

        var cached = _cache.Read(key, DateTimeOffset.UtcNow);
        if (cached is not null)
        {
            DebugLog.Write($"lyrics: cache {(cached.Found ? "hit" : "known miss")} for '{track.Title}'");
            return ToDocument(cached);
        }

        var record = await _client
            .FindAsync(track.Title, track.Artist, track.AlbumTitle, duration, cancellation)
            .ConfigureAwait(false);

        var entry = new CachedLyrics
        {
            Found = record is not null,
            SyncedLyrics = record?.SyncedLyrics,
            PlainLyrics = record?.PlainLyrics,
            FetchedUtc = DateTimeOffset.UtcNow,
            TrackName = record?.TrackName ?? track.Title,
            ArtistName = record?.ArtistName ?? track.Artist,
        };

        _cache.Write(key, entry);

        return ToDocument(entry);
    }

    /// <summary>
    /// Looks for a hand-supplied file, named for the track, in the cache folder.
    /// </summary>
    /// <remarks>
    /// The cache folder rather than beside the audio, because SMTC identifies a
    /// session by app id and never reveals a file path — for Spotify or a browser
    /// there is no local file to sit beside.
    /// </remarks>
    private LyricsDocument? ReadImported(TrackInfo track)
    {
        try
        {
            string name = Sanitize($"{track.Artist} - {track.Title}");
            string path = Path.Combine(_cache.DirectoryPath, $"{name}.lrc");

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

        // Timed lyrics if they exist, plain text if not. An instrumental has neither,
        // and correctly resolves to nothing to show.
        var synced = LrcParser.Parse(entry.SyncedLyrics);

        return synced.IsEmpty ? LrcParser.Parse(entry.PlainLyrics) : synced;
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
