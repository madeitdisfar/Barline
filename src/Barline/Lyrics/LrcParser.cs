using System.Text.RegularExpressions;

namespace Barline.Lyrics;

/// <summary>
/// Parses LRC, the de-facto format for timed lyrics.
/// </summary>
/// <remarks>
/// <para>
/// The format is barely specified and every producer bends it, so this is written to
/// salvage rather than validate: an unrecognized line is skipped, not fatal. A file
/// that is half malformed should still show the half that works.
/// </para>
/// <para>
/// Handles the three variations that actually occur — several timestamps on one line
/// for a repeated chorus, metadata tags sharing the bracket syntax with timestamps,
/// and the enhanced word-level extension.
/// </para>
/// </remarks>
internal static partial class LrcParser
{
    /// <summary>
    /// A line timestamp. Fractions are optional and may be two digits (centiseconds,
    /// the common case) or three (milliseconds).
    /// </summary>
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex LineStamp { get; }

    /// <summary>The enhanced extension's word timestamp, in angle brackets.</summary>
    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled)]
    private static partial Regex WordStamp { get; }

    /// <summary>
    /// Reads LRC text. Falls back to treating the input as plain lyrics when it
    /// carries no timestamps at all, so one path covers both of the fields LRCLIB
    /// returns.
    /// </summary>
    public static LyricsDocument Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LyricsDocument.Empty;

        var lines = new List<LyricLine>();

        foreach (string raw in SplitLines(text))
        {
            var stamps = LineStamp.Matches(raw);
            if (stamps.Count == 0) continue;

            // Metadata tags such as [ar:Radiohead] share the bracket syntax but never
            // start with a digit, so the timestamp pattern already excludes them.
            // What it cannot exclude is a stamp appearing mid-text, which would make
            // the content before it disappear.
            if (stamps[0].Index != 0) continue;

            string content = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            var (plain, words) = ExtractWords(content);

            // One line, several times: a chorus timed at each repeat.
            foreach (Match stamp in stamps)
            {
                lines.Add(new LyricLine
                {
                    Start = ToTimeSpan(stamp),
                    Text = plain,
                    // Word timings belong to the first occurrence only; reusing them
                    // for a later repeat would place every word in the past.
                    Words = ReferenceEquals(stamp, stamps[0]) ? words : null,
                });
            }
        }

        if (lines.Count == 0) return ParsePlain(text);

        // Repeated choruses arrive out of order, and some files are simply unsorted.
        lines.Sort((a, b) => a.Start.CompareTo(b.Start));

        return new LyricsDocument(lines, isSynced: true);
    }

    /// <summary>
    /// Untimed lyrics. Kept as a document so callers have one type to handle; the
    /// display shows it as a block rather than following the playback position.
    /// </summary>
    private static LyricsDocument ParsePlain(string text)
    {
        var lines = SplitLines(text)
            .Select(line => new LyricLine { Start = TimeSpan.Zero, Text = line.Trim() })
            .ToList();

        // Trailing blank lines are noise here, unlike in a timed file where a blank
        // line is a deliberate instrumental gap.
        while (lines.Count > 0 && lines[^1].IsBlank)
            lines.RemoveAt(lines.Count - 1);

        return lines.Count == 0
            ? LyricsDocument.Empty
            : new LyricsDocument(lines, isSynced: false);
    }

    /// <summary>
    /// Splits out the enhanced extension's word timings, returning the line as it
    /// should read alongside them.
    /// </summary>
    private static (string Plain, IReadOnlyList<LyricWord>? Words) ExtractWords(string content)
    {
        var stamps = WordStamp.Matches(content);
        if (stamps.Count == 0) return (content, null);

        var words = new List<LyricWord>(stamps.Count);

        for (int i = 0; i < stamps.Count; i++)
        {
            int from = stamps[i].Index + stamps[i].Length;
            int to = i + 1 < stamps.Count ? stamps[i + 1].Index : content.Length;

            string word = content[from..to].Trim();
            if (word.Length == 0) continue;

            words.Add(new LyricWord(ToTimeSpan(stamps[i]), word));
        }

        // The readable line is the same text with the markers taken back out, so a
        // caller that ignores word timing still gets something sensible.
        string plain = WordStamp.Replace(content, string.Empty);
        plain = string.Join(' ', plain.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return (plain, words.Count > 0 ? words : null);
    }

    private static TimeSpan ToTimeSpan(Match stamp)
    {
        int minutes = int.Parse(stamp.Groups[1].Value);
        int seconds = int.Parse(stamp.Groups[2].Value);

        double fraction = 0d;
        if (stamp.Groups[3].Success)
        {
            string digits = stamp.Groups[3].Value;
            // Two digits are hundredths, three are thousandths.
            fraction = int.Parse(digits) / Math.Pow(10d, digits.Length);
        }

        return TimeSpan.FromSeconds((minutes * 60d) + seconds + fraction);
    }

    private static IEnumerable<string> SplitLines(string text) =>
        // Trim the BOM: a file saved from a text editor otherwise fails to match a
        // timestamp on its very first line, silently losing it.
        text.TrimStart('﻿').Split('\n').Select(line => line.TrimEnd('\r'));
}
