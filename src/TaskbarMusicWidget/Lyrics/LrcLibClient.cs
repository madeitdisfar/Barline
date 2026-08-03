using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TaskbarMusicWidget.Diagnostics;

namespace TaskbarMusicWidget.Lyrics;

/// <summary>What LRCLIB returns for a track. Field names match the JSON exactly.</summary>
internal sealed class LrcLibRecord
{
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
    public double Duration { get; set; }
    public bool Instrumental { get; set; }
    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }

    [JsonIgnore]
    public bool HasAnything =>
        Instrumental ||
        !string.IsNullOrWhiteSpace(SyncedLyrics) ||
        !string.IsNullOrWhiteSpace(PlainLyrics);
}

/// <summary>
/// Fetches lyrics from LRCLIB.
/// </summary>
/// <remarks>
/// <para>
/// Chosen because it is the only free source that serves timed lyrics without an API
/// key, a paid tier, or terms that forbid this use. It is run at no charge for
/// exactly this purpose, which is a reason to be a careful client rather than a
/// greedy one: every result is cached, misses included, and a track is never asked
/// for twice in a session.
/// </para>
/// <para>
/// Two endpoints, tried in order. <c>/api/get</c> matches on duration and returns the
/// right recording of a track that has many; <c>/api/search</c> ignores duration and
/// is the fallback for when the reported length is slightly off, which is common for
/// browser sessions.
/// </para>
/// </remarks>
internal sealed class LrcLibClient : IDisposable
{
    private const string BaseAddress = "https://lrclib.net/";

    /// <summary>
    /// LRCLIB asks clients to identify themselves and link to the project, so that a
    /// misbehaving one can be recognised and contacted rather than simply blocked.
    /// </summary>
    private const string UserAgent =
        "TaskbarMusicWidget/1.0 (https://github.com/mjkim/Taskbar-Music-Widget)";

    /// <summary>
    /// How far a search result's length may differ from the track being played.
    /// Wide enough to absorb a browser rounding to whole seconds, narrow enough to
    /// reject a different recording of the same song.
    /// </summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(6);

    private readonly HttpClient _http;

    public LrcLibClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseAddress),
            // Lyrics are never worth stalling on. Failing fast just means the widget
            // shows none, which is the same as it did a moment ago.
            Timeout = TimeSpan.FromSeconds(8),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>
    /// Looks a track up, widening the query until something matches.
    /// </summary>
    /// <returns>The record, or null when the track genuinely has no lyrics filed.</returns>
    public async Task<LrcLibRecord?> FindAsync(
        string title,
        string artist,
        string album,
        TimeSpan duration,
        CancellationToken cancellation)
    {
        foreach (var candidate in TrackQuery.Candidates(title, artist))
        {
            var record = await GetAsync(candidate, album, duration, cancellation).ConfigureAwait(false);
            if (record is not null) return record;
        }

        // Everything above insists on a duration match. This does not, which is what
        // rescues a source that reports the length a second or two out.
        return await SearchAsync(title, artist, duration, cancellation).ConfigureAwait(false);
    }

    private async Task<LrcLibRecord?> GetAsync(
        TrackCandidate candidate,
        string album,
        TimeSpan duration,
        CancellationToken cancellation)
    {
        string url =
            $"api/get?artist_name={Escape(candidate.Artist)}" +
            $"&track_name={Escape(candidate.Title)}" +
            $"&album_name={Escape(album)}" +
            $"&duration={(int)Math.Round(duration.TotalSeconds)}";

        var record = await SendAsync(url, cancellation).ConfigureAwait(false);

        if (record?.HasAnything == true)
        {
            DebugLog.Write($"lyrics: matched '{candidate.Title}' by '{candidate.Artist}'");
            return record;
        }

        return null;
    }

    private async Task<LrcLibRecord?> SearchAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellation)
    {
        var candidate = TrackQuery.Candidates(title, artist).LastOrDefault();
        if (candidate.Title is null) return null;

        string url =
            $"api/search?track_name={Escape(candidate.Title)}" +
            $"&artist_name={Escape(candidate.Artist)}";

        LrcLibRecord[]? results;
        try
        {
            results = await _http
                .GetFromJsonAsync<LrcLibRecord[]>(url, cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugLog.Write($"lyrics: search failed: {ex.Message}");
            return null;
        }

        if (results is null || results.Length == 0) return null;

        // Closest length wins, since search ignores duration and a popular song has
        // live cuts, edits and covers filed under the same name.
        var best = results
            .Where(r => r.HasAnything)
            .Select(r => (Record: r, Gap: Math.Abs(r.Duration - duration.TotalSeconds)))
            .Where(x => x.Gap <= DurationTolerance.TotalSeconds)
            .OrderBy(x => x.Gap)
            .FirstOrDefault();

        if (best.Record is not null)
            DebugLog.Write($"lyrics: search matched '{best.Record.TrackName}' ({best.Gap:F1}s off)");

        return best.Record;
    }

    private async Task<LrcLibRecord?> SendAsync(string url, CancellationToken cancellation)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellation).ConfigureAwait(false);

            // The expected answer for a track nobody has contributed lyrics for.
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            response.EnsureSuccessStatusCode();

            return await response.Content
                .ReadFromJsonAsync<LrcLibRecord>(cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline, DNS failure, the service being down — all the same to us.
            DebugLog.Write($"lyrics: lookup failed: {ex.Message}");
            return null;
        }
    }

    private static string Escape(string? value) => Uri.EscapeDataString(value ?? string.Empty);

    public void Dispose() => _http.Dispose();
}
