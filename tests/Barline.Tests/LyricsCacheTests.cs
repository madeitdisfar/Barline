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
