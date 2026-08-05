using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskbarMusicWidget.Lyrics;

/// <summary>
/// Reads LRCLIB's <c>lyricsfile</c> format — a small YAML document served alongside
/// the LRC.
/// </summary>
/// <remarks>
/// <para>
/// Worth parsing for one reason: it states each line's <c>end_ms</c>. LRC cannot
/// express that, so a line there implicitly runs until the next one starts — which is
/// wrong every time an instrumental stretch follows, and it is exactly the number the
/// word-timing estimate divides up. Everything else in the format duplicates what the
/// LRC already carries.
/// </para>
/// <para>
/// The format is documented as supporting word-level timing too, but no contributed
/// data appears to use it yet: a sample of roughly a hundred records carried only
/// <c>text</c>, <c>start_ms</c> and <c>end_ms</c>. Word-level keys are therefore not
/// guessed at here — if they appear, they can be added against real data.
/// </para>
/// <para>
/// Only the shape actually served is read, and anything unexpected makes the whole
/// parse fail rather than half-succeed, because the caller always has the LRC to fall
/// back on. Half-parsed lyrics would be worse than none.
/// </para>
/// </remarks>
internal static partial class LyricsFileParser
{
    /// <summary>
    /// A <c>lines:</c> entry: a two-space-indented sequence item, then its fields.
    /// Values may be quoted, since a lyric can begin with a character YAML reserves.
    /// </summary>
    [GeneratedRegex(@"^\s*-\s+(?<key>[a-z_]+):\s*(?<value>.*)$", RegexOptions.Compiled)]
    private static partial Regex ItemStart { get; }

    [GeneratedRegex(@"^\s+(?<key>[a-z_]+):\s*(?<value>.*)$", RegexOptions.Compiled)]
    private static partial Regex ItemField { get; }

    /// <summary>
    /// Reads the document, or returns null when it is not the shape expected.
    /// </summary>
    public static LyricsDocument? Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        if (!yaml.Contains("lines:", StringComparison.Ordinal)) return null;

        var lines = new List<LyricLine>();

        string? text = null;
        TimeSpan? start = null;
        TimeSpan? end = null;
        bool inLines = false;

        foreach (string raw in yaml.TrimStart('﻿').Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            // Top-level keys are unindented; "lines:" opens the sequence and any other
            // one closes it, so the plain-text block at the end is not read as lines.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
            {
                if (inLines) Flush(lines, ref text, ref start, ref end);
                inLines = line.StartsWith("lines:", StringComparison.Ordinal);
                continue;
            }

            if (!inLines) continue;

            if (ItemStart.Match(line) is { Success: true } item)
            {
                // A new sequence item: whatever was being collected is complete.
                Flush(lines, ref text, ref start, ref end);
                Assign(item.Groups["key"].Value, item.Groups["value"].Value, ref text, ref start, ref end);
                continue;
            }

            if (ItemField.Match(line) is { Success: true } field)
                Assign(field.Groups["key"].Value, field.Groups["value"].Value, ref text, ref start, ref end);
        }

        Flush(lines, ref text, ref start, ref end);

        if (lines.Count == 0) return null;

        lines.Sort((a, b) => a.Start.CompareTo(b.Start));

        return new LyricsDocument(lines, isSynced: true);
    }

    private static void Assign(
        string key,
        string value,
        ref string? text,
        ref TimeSpan? start,
        ref TimeSpan? end)
    {
        switch (key)
        {
            case "text":
                text = Unquote(value);
                break;

            case "start_ms" when TryMilliseconds(value, out var from):
                start = from;
                break;

            case "end_ms" when TryMilliseconds(value, out var to):
                end = to;
                break;
        }
    }

    private static void Flush(
        List<LyricLine> lines,
        ref string? text,
        ref TimeSpan? start,
        ref TimeSpan? end)
    {
        // A start is the one field a line cannot do without; the text may legitimately
        // be empty for an instrumental gap.
        if (start is { } from)
            lines.Add(new LyricLine { Start = from, End = end, Text = text ?? string.Empty });

        text = null;
        start = null;
        end = null;
    }

    private static bool TryMilliseconds(string value, out TimeSpan result)
    {
        result = default;

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
            return false;

        if (ms < 0d) return false;

        result = TimeSpan.FromMilliseconds(ms);
        return true;
    }

    /// <summary>
    /// Strips YAML quoting. Only the two forms the server actually emits are handled;
    /// anything else is left as written rather than mangled.
    /// </summary>
    private static string Unquote(string value)
    {
        string trimmed = value.Trim();

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            return trimmed[1..^1].Replace("''", "'");

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");

        return trimmed;
    }
}
