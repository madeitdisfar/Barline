namespace TaskbarMusicWidget.Lyrics;

/// <summary>
/// Estimates when each word of a line is sung, for sources that only timed the line.
/// </summary>
/// <remarks>
/// <para>
/// No free service serves word-level timing, so a word-by-word display has to infer
/// it. The alternative — aligning the words to the audio properly — is not merely
/// expensive: it is causal. A forced aligner cannot know where a word lands until
/// after it has been sung, so displaying the result would mean running the lyrics
/// behind the music, which defeats the point regardless of how fast it ran.
/// </para>
/// <para>
/// Inference is cheap and, for the thing the eye actually notices, good enough. What
/// reads as wrong is a sweep that finishes early or overruns the line; what reads as
/// fine is a sweep that is a little off inside it. So the line's span is divided by
/// syllable count, which tracks singing time far better than character count does —
/// "strength" and "a potato" are the same length and nothing alike to sing.
/// </para>
/// </remarks>
internal static class WordTiming
{
    /// <summary>
    /// Longest a sweep may run. Past this the line is almost certainly followed by an
    /// instrumental stretch rather than being sung slowly, and stretching the words
    /// across it would leave the last one crawling for half a minute.
    /// </summary>
    private static readonly TimeSpan MaxSweep = TimeSpan.FromSeconds(9);

    /// <summary>
    /// Assumed pace when the line's end is unknown, which happens for the last line
    /// of a file.
    /// </summary>
    private static readonly TimeSpan PerSyllable = TimeSpan.FromMilliseconds(280);

    /// <summary>
    /// Splits a line into words with an estimated start for each.
    /// </summary>
    /// <param name="text">The line as it will be displayed.</param>
    /// <param name="start">When the line begins.</param>
    /// <param name="end">When the next line begins, or null if this is the last.</param>
    public static IReadOnlyList<LyricWord> Estimate(string text, TimeSpan start, TimeSpan? end)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var syllables = words.Select(CountSyllables).ToArray();
        int total = syllables.Sum();

        var span = Span(end - start, total);

        var timed = new LyricWord[words.Length];
        int sung = 0;

        for (int i = 0; i < words.Length; i++)
        {
            double fraction = (double)sung / total;
            timed[i] = new LyricWord(start + (span * fraction), words[i]);
            sung += syllables[i];
        }

        return timed;
    }

    /// <summary>How long the sweep should take, given what the file implies.</summary>
    private static TimeSpan Span(TimeSpan? available, int syllables)
    {
        var assumed = PerSyllable * syllables;

        // No following line to bound it: fall back to a plausible pace.
        if (available is not { } measured || measured <= TimeSpan.Zero) return assumed;

        return measured > MaxSweep ? MaxSweep : measured;
    }

    /// <summary>
    /// Counts syllables well enough to apportion time between words.
    /// </summary>
    /// <remarks>
    /// Vowel-group counting is an English heuristic and collapses on scripts that do
    /// not spell vowels separately: every Korean or Japanese line would come back as
    /// one syllable per word and the sweep would be worthless. Those scripts write one
    /// syllable per character, which is both more accurate and simpler, so they are
    /// counted that way and the heuristic is kept for the alphabetic remainder.
    /// </remarks>
    internal static int CountSyllables(string word)
    {
        int count = 0;
        bool inVowel = false;
        bool alphabetic = false;

        foreach (char c in word)
        {
            if (IsSyllabic(c))
            {
                count++;
                inVowel = false;
                continue;
            }

            if (!char.IsLetter(c)) continue;

            alphabetic = true;

            bool vowel = IsVowel(c);
            if (vowel && !inVowel) count++;
            inVowel = vowel;
        }

        // A trailing "e" is usually silent — "make" is one syllable, not two — but not
        // in the "-le" ending, where it carries its own: "little", "candle".
        if (alphabetic && count > 1 && EndsWithSilentE(word)) count--;

        return Math.Max(1, count);
    }

    /// <summary>
    /// Scripts that write one syllable (or mora) per character: Hangul syllable
    /// blocks, CJK ideographs, and the Japanese kana.
    /// </summary>
    private static bool IsSyllabic(char c) =>
        c is >= '가' and <= '힣' ||   // Hangul syllables
        c is >= '一' and <= '鿿' ||   // CJK unified ideographs
        c is >= '぀' and <= 'ヿ';     // Hiragana and katakana

    private static bool IsVowel(char c) =>
        char.ToLowerInvariant(c) is 'a' or 'e' or 'i' or 'o' or 'u' or 'y' ||
        // Accented forms, so lyrics in other Latin-script languages still divide up
        // sensibly rather than collapsing to one syllable a word.
        "àáâãäåèéêëìíîïòóôõöùúûüýÿ".Contains(char.ToLowerInvariant(c));

    private static bool EndsWithSilentE(string word)
    {
        string trimmed = word.TrimEnd('.', ',', '!', '?', ';', ':', '"', ')', '\'');

        return trimmed.Length > 2 &&
            char.ToLowerInvariant(trimmed[^1]) == 'e' &&
            char.ToLowerInvariant(trimmed[^2]) != 'l';
    }
}
