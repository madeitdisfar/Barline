using Barline.Lyrics;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// LRC parsing. The format is barely specified and every producer bends it, so these
/// tests are mostly about salvaging real-world files rather than honouring a spec.
/// </summary>
public class LrcParserTests
{
    private const string Sample =
        "[ar:Radiohead]\n" +
        "[ti:Creep]\n" +
        "[00:19.16] When you were here before\n" +
        "[00:24.09]Couldn't look you in the eye\n" +
        "[00:29.24] You're just like an angel\n";

    [Fact]
    public void Timed_lines_are_read_with_their_times()
    {
        var document = LrcParser.Parse(Sample);

        Assert.True(document.IsSynced);
        Assert.Equal(3, document.Lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(19.16), document.Lines[0].Start);
        Assert.Equal("When you were here before", document.Lines[0].Text);

        // Whether a space follows the timestamp is down to the producer.
        Assert.Equal("Couldn't look you in the eye", document.Lines[1].Text);
    }

    /// <summary>Metadata tags share the bracket syntax and must not become lines.</summary>
    [Fact]
    public void Metadata_tags_are_not_mistaken_for_lyrics()
    {
        var document = LrcParser.Parse(Sample);

        Assert.DoesNotContain(document.Lines, line => line.Text.Contains("Radiohead"));
        Assert.DoesNotContain(document.Lines, line => line.Text.Contains("ti:"));
    }

    [Theory]
    [InlineData("[01:02]Text", 62d)]
    [InlineData("[01:02.5]Text", 62.5d)]
    [InlineData("[01:02.50]Text", 62.5d)]
    [InlineData("[01:02.500]Text", 62.5d)]
    [InlineData("[01:02:50]Text", 62.5d)]
    [InlineData("[100:00.00]Text", 6000d)]
    public void Timestamps_are_read_in_every_form_that_occurs(string line, double expectedSeconds)
    {
        var document = LrcParser.Parse(line);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), document.Lines[0].Start);
    }

    /// <summary>A chorus is often written once and timed at each repeat.</summary>
    [Fact]
    public void A_line_timed_several_times_becomes_several_lines()
    {
        var document = LrcParser.Parse("[00:30.00][01:30.00][02:30.00]We all float\n");

        Assert.Equal(3, document.Lines.Count);
        Assert.All(document.Lines, line => Assert.Equal("We all float", line.Text));
        Assert.Equal(TimeSpan.FromSeconds(150), document.Lines[2].Start);
    }

    [Fact]
    public void Lines_come_back_in_time_order_however_the_file_was_written()
    {
        var document = LrcParser.Parse(
            "[02:00.00]third\n" +
            "[00:10.00]first\n" +
            "[01:00.00]second\n");

        Assert.Equal(["first", "second", "third"], document.Lines.Select(l => l.Text));
    }

    /// <summary>
    /// A timed line with no words is a deliberate gap, and dropping it would leave
    /// the previous line on screen through an instrumental break.
    /// </summary>
    [Fact]
    public void An_empty_timed_line_is_kept_as_a_gap()
    {
        var document = LrcParser.Parse("[00:10.00]words\n[00:20.00]\n");

        Assert.Equal(2, document.Lines.Count);
        Assert.True(document.Lines[1].IsBlank);
    }

    [Fact]
    public void Text_with_no_timestamps_is_read_as_plain_lyrics()
    {
        var document = LrcParser.Parse("When you were here before\nCouldn't look you in the eye\n");

        Assert.False(document.IsSynced);
        Assert.Equal(2, document.Lines.Count);
        Assert.Equal(-1, document.IndexAt(TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    public void Nothing_in_means_nothing_out(string? text)
    {
        Assert.True(LrcParser.Parse(text).IsEmpty);
    }

    /// <summary>
    /// A byte-order mark otherwise sits in front of the first timestamp and silently
    /// costs the file its opening line.
    /// </summary>
    [Fact]
    public void A_byte_order_mark_does_not_eat_the_first_line()
    {
        var document = LrcParser.Parse("﻿[00:01.00]first\n[00:02.00]second\n");

        Assert.Equal(2, document.Lines.Count);
        Assert.Equal("first", document.Lines[0].Text);
    }

    [Fact]
    public void Windows_line_endings_do_not_leave_carriage_returns_in_the_text()
    {
        var document = LrcParser.Parse("[00:01.00]first\r\n[00:02.00]second\r\n");

        Assert.Equal("first", document.Lines[0].Text);
        Assert.DoesNotContain(document.Lines, line => line.Text.Contains('\r'));
    }

    /// <summary>Salvage over validation: one bad line must not lose the good ones.</summary>
    [Fact]
    public void A_malformed_line_is_skipped_rather_than_failing_the_file()
    {
        var document = LrcParser.Parse(
            "[00:01.00]good\n" +
            "this line has no timestamp at all\n" +
            "some text [00:05.00] with a stamp in the middle\n" +
            "[00:09.00]also good\n");

        Assert.Equal(["good", "also good"], document.Lines.Select(l => l.Text));
    }

    // ---- Enhanced (word-level) LRC -----------------------------------------

    [Fact]
    public void Word_timings_are_read_when_the_file_carries_them()
    {
        var document = LrcParser.Parse("[00:10.00]<00:10.00>When <00:10.50>you <00:11.00>were\n");

        var words = document.Lines[0].Words;
        Assert.NotNull(words);
        Assert.Equal(3, words!.Count);
        Assert.Equal("you", words[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(10.5), words[1].Start);
    }

    /// <summary>
    /// The line must still read normally for anything that ignores word timing.
    /// </summary>
    [Fact]
    public void An_enhanced_line_still_reads_as_plain_text()
    {
        var document = LrcParser.Parse("[00:10.00]<00:10.00>When <00:10.50>you <00:11.00>were\n");

        Assert.Equal("When you were", document.Lines[0].Text);
    }

    /// <summary>
    /// Word times belong to the occurrence they were written for. Copying them onto a
    /// later repeat would place every word of that chorus in the past.
    /// </summary>
    [Fact]
    public void Word_timings_are_not_copied_onto_a_repeated_chorus()
    {
        var document = LrcParser.Parse("[00:30.00][01:30.00]<00:30.00>We <00:30.40>float\n");

        Assert.NotNull(document.Lines[0].Words);
        Assert.Null(document.Lines[1].Words);
    }

    // ---- Lookup ------------------------------------------------------------

    [Fact]
    public void The_line_showing_is_the_last_one_to_have_started()
    {
        var document = LrcParser.Parse(Sample);

        Assert.Equal(-1, document.IndexAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, document.IndexAt(TimeSpan.FromSeconds(19.16)));
        Assert.Equal(0, document.IndexAt(TimeSpan.FromSeconds(24.08)));
        Assert.Equal(1, document.IndexAt(TimeSpan.FromSeconds(24.09)));
        Assert.Equal(2, document.IndexAt(TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// Lookup is polled once a frame and must not depend on what was asked before,
    /// because a seek can move the position anywhere between two calls.
    /// </summary>
    [Fact]
    public void Lookup_does_not_depend_on_the_order_it_is_asked_in()
    {
        var document = LrcParser.Parse(Sample);

        Assert.Equal(2, document.IndexAt(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, document.IndexAt(TimeSpan.FromSeconds(20)));
        Assert.Equal(2, document.IndexAt(TimeSpan.FromSeconds(30)));
        Assert.Equal(-1, document.IndexAt(TimeSpan.Zero));
    }

    [Fact]
    public void A_line_ends_where_the_next_one_starts_and_the_last_one_is_open_ended()
    {
        var document = LrcParser.Parse(Sample);

        Assert.Equal(TimeSpan.FromSeconds(24.09), document.EndOf(0));
        Assert.Null(document.EndOf(2));
        Assert.Null(document.EndOf(-1));
    }
}
