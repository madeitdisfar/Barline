using System.Text.RegularExpressions;

namespace Barline.Lyrics;

/// <summary>One attempt at naming the track for a lyrics lookup.</summary>
internal readonly record struct TrackCandidate(string Title, string Artist);

/// <summary>
/// Turns what a media session reports into the names a lyrics database is likely to
/// be filed under.
/// </summary>
/// <remarks>
/// <para>
/// This is where lyrics lookups are actually won or lost. Coverage is rarely the
/// problem — matching is. Spotify reports <c>Creep - Remastered 2011</c>, a browser
/// reports <c>Radiohead - Creep (Official Video)</c>, and neither string appears in
/// any database as written, so a naive lookup misses tracks that are plainly there.
/// </para>
/// <para>
/// Candidates are yielded from most to least faithful rather than normalising once
/// and hoping. The unedited name is tried first, because stripping is a guess and a
/// track really can be called <c>Live</c>; only when that fails is the guess made.
/// </para>
/// </remarks>
internal static partial class TrackQuery
{
    /// <summary>
    /// Words that mark a bracketed group or trailing clause as packaging rather than
    /// part of the title.
    /// </summary>
    private const string NoiseWords =
        @"official|music\s+video|lyrics?|audio|video|visuali[sz]er|hd|hq|4k|8k|" +
        @"remaster(ed)?|re-?master|remix|explicit|clean|radio\s+edit|extended|" +
        @"single\s+version|album\s+version|original\s+mix|live|demo|mono|stereo|" +
        @"deluxe|bonus|anniversary|edition|reissue|feat\.?|ft\.?|featuring|with";

    /// <summary>A bracketed group that is entirely packaging.</summary>
    [GeneratedRegex(
        @"[\(\[\{]\s*(?:" + NoiseWords + @")\b[^\)\]\}]*[\)\]\}]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BracketedNoise { get; }

    /// <summary>
    /// A trailing clause after a dash, as Spotify formats remasters and live cuts.
    /// Anchored to the end so a dash inside the real title is left alone.
    /// </summary>
    [GeneratedRegex(
        @"\s+[-–—]\s+[^-–—]*\b(?:" + NoiseWords + @")\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TrailingNoise { get; }

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex RepeatedSpace { get; }

    /// <summary>
    /// Separators that reliably introduce a second artist. Deliberately excludes
    /// <c>&amp;</c> and <c>+</c>, which appear inside real band names far too often
    /// to treat as a list.
    /// </summary>
    [GeneratedRegex(
        @"\s*(?:,|;|/|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ArtistSeparator { get; }

    /// <summary>
    /// Names to try, in order, stopping at the first that finds lyrics.
    /// </summary>
    public static IEnumerable<TrackCandidate> Candidates(string title, string artist)
    {
        title = Collapse(title);
        artist = Collapse(artist);

        if (title.Length == 0) yield break;

        var seen = new HashSet<TrackCandidate>();

        string cleanTitle = Clean(title);
        string primaryArtist = PrimaryArtist(artist);

        foreach (var candidate in new[]
        {
            new TrackCandidate(title, artist),
            new TrackCandidate(cleanTitle, artist),
            new TrackCandidate(cleanTitle, primaryArtist),
        })
        {
            if (candidate.Title.Length == 0) continue;
            if (seen.Add(candidate)) yield return candidate;
        }
    }

    /// <summary>Strips packaging from a title.</summary>
    public static string Clean(string title)
    {
        string cleaned = BracketedNoise.Replace(title, " ");
        cleaned = TrailingNoise.Replace(cleaned, string.Empty);
        cleaned = Collapse(cleaned);

        // Stripping everything means the guess was wrong — a track really can be
        // called "(Remastered)". Keep the original rather than searching for nothing.
        return cleaned.Length == 0 ? Collapse(title) : cleaned;
    }

    /// <summary>The first credited artist, for when the full credit finds nothing.</summary>
    public static string PrimaryArtist(string artist)
    {
        var parts = ArtistSeparator.Split(artist);

        foreach (string part in parts)
        {
            string trimmed = Collapse(part);
            if (trimmed.Length > 0) return trimmed;
        }

        return Collapse(artist);
    }

    private static string Collapse(string? value) =>
        value is null ? string.Empty : RepeatedSpace.Replace(value.Trim(), " ");
}
