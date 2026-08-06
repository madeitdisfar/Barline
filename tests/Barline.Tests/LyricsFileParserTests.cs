using Barline.Lyrics;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// LRCLIB's lyricsfile format. Parsed for one thing the LRC cannot express — where a
/// line stops being sung — so these tests are mostly about that field surviving, and
/// about the parser refusing rather than half-succeeding when the shape is wrong.
/// </summary>
public class LyricsFileParserTests
{
    /// <summary>Trimmed from a real LRCLIB response.</summary>
    private const string Sample = """
        version: '1.0'
        metadata:
          title: Creep
          artist: Radiohead
          duration_ms: 239000
          instrumental: false
        lines:
        - text: When you were here before
          start_ms: 19160
          end_ms: 24090
        - text: Couldn't look you in the eye
          start_ms: 24090
          end_ms: 29240
        - text: ''
          start_ms: 29240
          end_ms: 48000
        plain: |-
          When you were here before
          Couldn't look you in the eye
        """;

    [Fact]
    public void Lines_are_read_with_their_starts_and_ends()
    {
        var document = LyricsFileParser.Parse(Sample);

        Assert.NotNull(document);
        Assert.True(document!.IsSynced);
        Assert.Equal(3, document.Lines.Count);

        Assert.Equal(TimeSpan.FromMilliseconds(19160), document.Lines[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(24090), document.Lines[0].End);
        Assert.Equal("When you were here before", document.Lines[0].Text);
    }

    /// <summary>
    /// The whole reason this format is parsed. An LRC line implicitly runs until the
    /// next one starts; here the source says when it actually stops.
    /// </summary>
    [Fact]
    public void A_stated_end_is_used_in_preference_to_the_next_lines_start()
    {
        var document = LyricsFileParser.Parse(Sample)!;

        // The third line runs 29.24s to 48s, but the next line does not exist — and
        // even where it does, the stated end is the one that counts.
        Assert.Equal(TimeSpan.FromMilliseconds(48000), document.EndOf(2));
        Assert.Equal(TimeSpan.FromMilliseconds(24090), document.EndOf(0));
    }

    /// <summary>
    /// The trailing plain-text block is indented like a line body. Reading it as
    /// lyrics would append the whole song again, untimed.
    /// </summary>
    [Fact]
    public void The_trailing_plain_text_block_is_not_read_as_lines()
    {
        var document = LyricsFileParser.Parse(Sample)!;

        Assert.Equal(3, document.Lines.Count);
        Assert.DoesNotContain(document.Lines, line => line.Text.Contains("plain"));
    }

    [Fact]
    public void An_instrumental_gap_survives_as_an_empty_line()
    {
        var document = LyricsFileParser.Parse(Sample)!;

        Assert.True(document.Lines[2].IsBlank);
        Assert.Equal(TimeSpan.FromMilliseconds(29240), document.Lines[2].Start);
    }

    [Theory]
    [InlineData("text: 'Don''t go'", "Don't go")]
    [InlineData("text: \"Say \\\"hello\\\"\"", "Say \"hello\"")]
    [InlineData("text: plain words", "plain words")]
    public void Quoted_values_are_unwrapped(string field, string expected)
    {
        var document = LyricsFileParser.Parse($"lines:\n- {field.TrimStart()}\n  start_ms: 1000\n");

        Assert.Equal(expected, document!.Lines[0].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("version: '1.0'\nmetadata:\n  title: Nothing\n")]
    [InlineData("this is not yaml at all")]
    public void Anything_that_is_not_this_format_is_refused_outright(string? yaml)
    {
        // Refusing lets the caller fall back to the LRC. Half-parsed lyrics would be
        // worse than none, because nothing downstream could tell.
        Assert.Null(LyricsFileParser.Parse(yaml));
    }

    [Fact]
    public void Lines_come_back_in_time_order()
    {
        var document = LyricsFileParser.Parse(
            "lines:\n" +
            "- text: second\n  start_ms: 2000\n" +
            "- text: first\n  start_ms: 1000\n")!;

        Assert.Equal(["first", "second"], document.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_line_without_a_start_is_dropped_rather_than_placed_at_zero()
    {
        var document = LyricsFileParser.Parse(
            "lines:\n" +
            "- text: untimed\n" +
            "- text: timed\n  start_ms: 5000\n")!;

        Assert.Single(document.Lines);
        Assert.Equal("timed", document.Lines[0].Text);
    }
}
