namespace TaskbarMusicWidget.Lyrics;

/// <summary>
/// One word within a line, when the source carries word-level timing.
/// </summary>
/// <remarks>
/// Only enhanced LRC files supply these, and no free service serves them at scale.
/// They are parsed when present so an imported file is not degraded, but the normal
/// path estimates word timing from the line instead.
/// </remarks>
internal readonly record struct LyricWord(TimeSpan Start, string Text);

/// <summary>A single lyric line and the moment it begins.</summary>
internal sealed record LyricLine
{
    public TimeSpan Start { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>Word timings, or null when the source only timed the line.</summary>
    public IReadOnlyList<LyricWord>? Words { get; init; }

    /// <summary>
    /// True for a timed line with no words — an instrumental break or an outro.
    /// Worth keeping rather than dropping: it is what tells the display to clear
    /// instead of leaving the previous line up through a thirty-second solo.
    /// </summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// A parsed set of lyrics, ready to be looked up by playback position.
/// </summary>
internal sealed class LyricsDocument
{
    /// <summary>Lines in time order.</summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>
    /// False when the source had no timings at all, in which case <see cref="Lines"/>
    /// is the plain text split into lines and only readable as a block.
    /// </summary>
    public bool IsSynced { get; }

    public LyricsDocument(IReadOnlyList<LyricLine> lines, bool isSynced)
    {
        Lines = lines;
        IsSynced = isSynced;
    }

    public static LyricsDocument Empty { get; } = new([], isSynced: false);

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// Index of the line that should be showing at <paramref name="position"/>, or -1
    /// before the first line starts.
    /// </summary>
    /// <remarks>
    /// Binary search rather than a scan, and no state kept between calls. This is
    /// polled once a frame, and a cursor that remembered where it was last would have
    /// to be unwound on every seek — which is exactly when being wrong is most
    /// visible.
    /// </remarks>
    public int IndexAt(TimeSpan position)
    {
        if (!IsSynced || Lines.Count == 0) return -1;
        if (position < Lines[0].Start) return -1;

        int low = 0;
        int high = Lines.Count - 1;

        while (low < high)
        {
            // Upper midpoint, so the search converges on the last line at or before
            // the position rather than looping.
            int mid = low + ((high - low + 1) / 2);

            if (Lines[mid].Start <= position) low = mid;
            else high = mid - 1;
        }

        return low;
    }

    /// <summary>
    /// When the line at <paramref name="index"/> gives way to the next one. Returns
    /// null for the final line, whose end is not knowable from the file.
    /// </summary>
    public TimeSpan? EndOf(int index) =>
        index >= 0 && index + 1 < Lines.Count ? Lines[index + 1].Start : null;
}
