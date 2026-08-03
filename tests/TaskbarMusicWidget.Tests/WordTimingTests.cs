using TaskbarMusicWidget.Lyrics;
using Xunit;

namespace TaskbarMusicWidget.Tests;

/// <summary>
/// Word-timing estimation. No free source carries word-level timing, so the
/// word-by-word display rests entirely on inferring it from the line.
/// </summary>
public class WordTimingTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(10);

    // ---- Syllables ---------------------------------------------------------

    [Theory]
    [InlineData("a", 1)]
    [InlineData("the", 1)]
    [InlineData("hello", 2)]
    [InlineData("beautiful", 3)]
    [InlineData("strength", 1)]
    [InlineData("everything", 4)]
    public void English_words_are_counted_by_vowel_group(string word, int expected)
    {
        Assert.Equal(expected, WordTiming.CountSyllables(word));
    }

    /// <summary>"make" is one syllable, but "little" really is two.</summary>
    [Theory]
    [InlineData("make", 1)]
    [InlineData("gone", 1)]
    [InlineData("time", 1)]
    [InlineData("little", 2)]
    [InlineData("candle", 2)]
    public void A_trailing_e_is_silent_except_after_l(string word, int expected)
    {
        Assert.Equal(expected, WordTiming.CountSyllables(word));
    }

    /// <summary>
    /// The case vowel-group counting cannot handle at all. Korean and Japanese spell
    /// a syllable per character, so counting Latin vowels would return one syllable
    /// for an entire word and make the sweep meaningless.
    /// </summary>
    [Theory]
    [InlineData("사랑해", 3)]
    [InlineData("안녕", 2)]
    [InlineData("こんにちは", 5)]
    [InlineData("音楽", 2)]
    public void Syllabic_scripts_are_counted_per_character(string word, int expected)
    {
        Assert.Equal(expected, WordTiming.CountSyllables(word));
    }

    [Theory]
    [InlineData("don't", 1)]
    [InlineData("i'm", 1)]
    [InlineData("(gone)", 1)]
    [InlineData("...", 1)]
    [InlineData("", 1)]
    public void Punctuation_never_produces_a_zero_count(string word, int expected)
    {
        Assert.Equal(expected, WordTiming.CountSyllables(word));
    }

    // ---- Distribution ------------------------------------------------------

    [Fact]
    public void The_first_word_starts_when_the_line_does()
    {
        var words = WordTiming.Estimate("one two three", Start, Start + TimeSpan.FromSeconds(3));

        Assert.Equal(Start, words[0].Start);
    }

    [Fact]
    public void Every_word_is_kept_in_order()
    {
        var words = WordTiming.Estimate("the quick brown fox", Start, Start + TimeSpan.FromSeconds(4));

        Assert.Equal(["the", "quick", "brown", "fox"], words.Select(w => w.Text));

        for (int i = 1; i < words.Count; i++)
            Assert.True(words[i].Start >= words[i - 1].Start, $"word {i} starts before {i - 1}");
    }

    /// <summary>
    /// The sweep must not overrun the line. Finishing late is the failure the eye
    /// notices, because the highlight is still crawling when the next line appears.
    /// </summary>
    [Fact]
    public void The_last_word_starts_before_the_line_ends()
    {
        var end = Start + TimeSpan.FromSeconds(4);
        var words = WordTiming.Estimate("the quick brown fox jumps over", Start, end);

        Assert.True(words[^1].Start < end);
    }

    /// <summary>
    /// Time is apportioned by syllables, not by word count — a long word must take
    /// longer than a short one beside it.
    /// </summary>
    [Fact]
    public void A_longer_word_is_given_more_time_than_a_short_one()
    {
        var words = WordTiming.Estimate("a beautiful cat", Start, Start + TimeSpan.FromSeconds(6));

        var first = words[1].Start - words[0].Start;
        var second = words[2].Start - words[1].Start;

        // "a" is one syllable, "beautiful" is three.
        Assert.True(second > first, $"'beautiful' got {second.TotalSeconds:F2}s against 'a' at {first.TotalSeconds:F2}s");
    }

    /// <summary>
    /// A line followed by a long instrumental stretch must not have its last word
    /// crawling for half a minute.
    /// </summary>
    [Fact]
    public void A_line_before_a_long_gap_sweeps_and_stops_rather_than_stretching()
    {
        var words = WordTiming.Estimate(
            "one two three",
            Start,
            Start + TimeSpan.FromSeconds(45));

        Assert.True(
            words[^1].Start - Start < TimeSpan.FromSeconds(10),
            $"the sweep was stretched across {(words[^1].Start - Start).TotalSeconds:F0}s");
    }

    /// <summary>The final line of a file has no following line to bound it.</summary>
    [Fact]
    public void A_line_with_no_known_end_still_gets_a_plausible_sweep()
    {
        var words = WordTiming.Estimate("one two three", Start, end: null);

        Assert.Equal(3, words.Count);
        Assert.Equal(Start, words[0].Start);
        Assert.True(words[^1].Start > Start);
        Assert.True(words[^1].Start - Start < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void An_empty_line_has_no_words()
    {
        Assert.Empty(WordTiming.Estimate("   ", Start, Start + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void A_single_word_line_starts_immediately()
    {
        var words = WordTiming.Estimate("oh", Start, Start + TimeSpan.FromSeconds(2));

        Assert.Single(words);
        Assert.Equal(Start, words[0].Start);
    }
}
