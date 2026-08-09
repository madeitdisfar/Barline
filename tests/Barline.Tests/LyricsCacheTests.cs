using System.IO;
using Barline.Lyrics;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// When a track that had no lyrics is asked about again.
/// </summary>
/// <remarks>
/// LRCLIB is contributed, so "no lyrics" is a statement about today rather than about
/// the track. Getting this wrong is silent in both directions: too eager and the widget
/// hammers a service run for free, too patient and a song stays blank for months after
/// somebody filed the words for it.
/// </remarks>
public class LyricsCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static CachedLyrics Miss(int misses, TimeSpan ago) => new()
    {
        Found = false,
        Misses = misses,
        FetchedUtc = Now - ago,
        Schema = LyricsCache.CurrentSchema,
    };

    [Fact]
    public void A_track_with_no_lyrics_is_not_asked_about_again_the_same_day()
    {
        Assert.True(LyricsCache.IsUsable(Miss(misses: 1, ago: TimeSpan.FromHours(3)), Now));
    }

    /// <summary>
    /// The case the whole backoff exists for: a song released this week has nothing
    /// filed the day it comes out, and does a day later.
    /// </summary>
    [Fact]
    public void A_first_miss_is_retried_the_next_day()
    {
        Assert.False(LyricsCache.IsUsable(Miss(misses: 1, ago: TimeSpan.FromDays(2)), Now));
    }

    [Fact]
    public void Each_empty_answer_pushes_the_next_attempt_further_out()
    {
        var delays = Enumerable
            .Range(1, LyricsCache.MissBackoff.Length + 3)
            .Select(LyricsCache.RetryAfter)
            .ToList();

        Assert.Equal(delays.OrderBy(delay => delay), delays);
        Assert.Equal(LyricsCache.MissBackoff[0], delays[0]);
    }

    /// <summary>
    /// However many times it has come back empty, the ladder tops out rather than
    /// running away — the cost of asking once more about a track nobody will ever file
    /// lyrics for is one request a month.
    /// </summary>
    [Fact]
    public void A_miss_never_becomes_permanent()
    {
        var ceiling = LyricsCache.MissBackoff[^1];

        Assert.Equal(ceiling, LyricsCache.RetryAfter(1000));
        Assert.False(LyricsCache.IsUsable(Miss(misses: 1000, ago: ceiling * 2), Now));
    }

    /// <summary>
    /// An entry written before misses were counted. Zero is not a miss count it earned,
    /// so it must not be read as one that has run out of patience.
    /// </summary>
    [Fact]
    public void An_entry_written_before_misses_were_counted_is_retried_soon()
    {
        Assert.Equal(LyricsCache.MissBackoff[0], LyricsCache.RetryAfter(0));
    }

    /// <summary>Lyrics for a released track do not change, so a hit is kept for good.</summary>
    [Fact]
    public void A_hit_is_never_expired()
    {
        var hit = new CachedLyrics
        {
            Found = true,
            SyncedLyrics = "[00:01.00]something",
            FetchedUtc = Now - TimeSpan.FromDays(4000),
            Schema = LyricsCache.CurrentSchema,
        };

        Assert.True(LyricsCache.IsUsable(hit, Now));
    }

    /// <summary>
    /// An entry from before a field we now store existed is fetched once more, so the
    /// improvement reaches tracks that were already cached.
    /// </summary>
    [Fact]
    public void An_entry_from_an_older_schema_is_fetched_again()
    {
        var stale = new CachedLyrics
        {
            Found = true,
            FetchedUtc = Now,
            Schema = LyricsCache.CurrentSchema - 1,
        };

        Assert.False(LyricsCache.IsUsable(stale, Now));
    }
}

/// <summary>
/// Splitting the cache out of the folder people put their own lyrics in.
/// </summary>
/// <remarks>
/// The whole point of the split is that one folder is disposable and the other is not,
/// so the move is only correct if it takes every fetched entry and leaves every
/// imported file exactly where it was.
/// </remarks>
public class LyricsCacheMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"barline-migrate-{Guid.NewGuid():N}");

    private string Legacy => Path.Combine(_root, "lyrics");
    private string Cache => Path.Combine(_root, "cache", "lyrics");

    private void Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }

    [Fact]
    public void Fetched_entries_move_and_imported_files_stay()
    {
        Write(Legacy, "a1b2c3.json", "{}");
        Write(Legacy, "d4e5f6.json", "{}");
        Write(Legacy, "Radiohead - Nude.lrc", "[00:12.00]line");

        Assert.Equal(2, LyricsCache.MigrateLegacyEntries(Legacy, Cache));

        Assert.True(File.Exists(Path.Combine(Cache, "a1b2c3.json")));
        Assert.True(File.Exists(Path.Combine(Cache, "d4e5f6.json")));

        Assert.Equal(
            ["Radiohead - Nude.lrc"],
            Directory.EnumerateFiles(Legacy).Select(Path.GetFileName));
    }

    /// <summary>
    /// Both copies are fetched results, so the stray one goes rather than being kept
    /// or overwriting what is already in place.
    /// </summary>
    [Fact]
    public void An_entry_already_in_the_cache_is_not_overwritten()
    {
        Write(Legacy, "a1b2c3.json", "old");
        Write(Cache, "a1b2c3.json", "current");

        _ = LyricsCache.MigrateLegacyEntries(Legacy, Cache);

        Assert.Equal("current", File.ReadAllText(Path.Combine(Cache, "a1b2c3.json")));
        Assert.Empty(Directory.EnumerateFiles(Legacy));
    }

    /// <summary>
    /// Runs on every launch, so the ordinary case is a fresh install or a folder that
    /// has already been dealt with. Neither may create anything.
    /// </summary>
    [Fact]
    public void Nothing_to_move_leaves_no_folder_behind()
    {
        Assert.Equal(0, LyricsCache.MigrateLegacyEntries(Legacy, Cache));
        Assert.False(Directory.Exists(Cache));

        Write(Legacy, "Radiohead - Nude.lrc", "[00:12.00]line");

        Assert.Equal(0, LyricsCache.MigrateLegacyEntries(Legacy, Cache));
        Assert.False(Directory.Exists(Cache));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that outlives the run is not a failed test.
        }
    }
}
