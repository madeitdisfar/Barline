using Barline.Lyrics;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Track-name normalization, which is what decides the hit rate. Coverage is rarely
/// the reason a lyrics lookup fails — the name the session reports simply is not the
/// name the track is filed under.
/// </summary>
public class TrackQueryTests
{
    [Theory]
    // Spotify's remaster and version suffixes.
    [InlineData("Creep - Remastered 2011", "Creep")]
    [InlineData("Bohemian Rhapsody - 2011 Remaster", "Bohemian Rhapsody")]
    [InlineData("Blue Monday - 2016 Remaster", "Blue Monday")]
    [InlineData("Song - Single Version", "Song")]
    [InlineData("Song - Radio Edit", "Song")]
    [InlineData("Song - Live at Wembley", "Song")]
    // What a browser reports.
    [InlineData("Creep (Official Video)", "Creep")]
    [InlineData("Creep (Official Music Video)", "Creep")]
    [InlineData("Creep [HD]", "Creep")]
    [InlineData("Song (Lyrics)", "Song")]
    [InlineData("Song (Visualizer)", "Song")]
    // Featured artists, which databases usually file without.
    [InlineData("Song (feat. Someone)", "Song")]
    [InlineData("Song (ft. Someone)", "Song")]
    [InlineData("Song (with Someone)", "Song")]
    public void Packaging_is_stripped_from_the_title(string reported, string expected)
    {
        Assert.Equal(expected, TrackQuery.Clean(reported));
    }

    [Theory]
    // A dash inside the real title is not a suffix.
    [InlineData("Marie-Anne")]
    [InlineData("Sgt. Pepper's Lonely Hearts Club Band")]
    [InlineData("Everything In Its Right Place")]
    // Nor is a bracketed group that is part of the name.
    [InlineData("Push It (Salt-N-Pepa)")]
    public void A_title_with_nothing_to_strip_is_left_alone(string title)
    {
        Assert.Equal(title, TrackQuery.Clean(title));
    }

    /// <summary>
    /// Stripping is a guess, and a track really can be named after the words we treat
    /// as packaging. Reducing a title to nothing means the guess was wrong.
    /// </summary>
    [Theory]
    [InlineData("(Remastered)")]
    [InlineData("(Live)")]
    public void A_title_that_is_entirely_packaging_is_kept_as_it_was(string title)
    {
        Assert.Equal(title, TrackQuery.Clean(title));
    }

    [Theory]
    [InlineData("Radiohead", "Radiohead")]
    [InlineData("Rosa Walton, Hallie Coggins", "Rosa Walton")]
    [InlineData("Artist; Other", "Artist")]
    [InlineData("Artist feat. Guest", "Artist")]
    [InlineData("Artist ft. Guest", "Artist")]
    [InlineData("Artist featuring Guest", "Artist")]
    public void The_primary_artist_is_the_first_credited(string reported, string expected)
    {
        Assert.Equal(expected, TrackQuery.PrimaryArtist(reported));
    }

    /// <summary>
    /// Ampersands and plus signs appear inside real band names far too often to treat
    /// as a credit list. Splitting on them would break more than it fixed.
    /// </summary>
    [Theory]
    [InlineData("Simon & Garfunkel")]
    [InlineData("Florence + the Machine")]
    [InlineData("Earth, Wind & Fire")]
    public void A_band_name_is_not_split_on_an_ampersand(string artist)
    {
        Assert.StartsWith(artist.Split(',')[0], TrackQuery.PrimaryArtist(artist));
    }

    /// <summary>
    /// The unedited name goes first. Stripping is a guess, so it must never pre-empt
    /// a name that would have matched as reported.
    /// </summary>
    [Fact]
    public void The_name_as_reported_is_tried_before_any_guess()
    {
        var candidates = TrackQuery.Candidates("Creep - Remastered 2011", "Radiohead").ToList();

        Assert.Equal("Creep - Remastered 2011", candidates[0].Title);
        Assert.Equal("Radiohead", candidates[0].Artist);
    }

    [Fact]
    public void Candidates_widen_from_exact_to_loose()
    {
        var candidates = TrackQuery
            .Candidates("Song (Official Video)", "Artist feat. Guest")
            .ToList();

        Assert.Equal(3, candidates.Count);
        Assert.Equal(new TrackCandidate("Song (Official Video)", "Artist feat. Guest"), candidates[0]);
        Assert.Equal(new TrackCandidate("Song", "Artist feat. Guest"), candidates[1]);
        Assert.Equal(new TrackCandidate("Song", "Artist"), candidates[2]);
    }

    /// <summary>
    /// A clean title by a single artist needs one lookup, not three identical ones —
    /// this runs against someone else's free service.
    /// </summary>
    [Fact]
    public void A_name_with_nothing_to_strip_produces_a_single_lookup()
    {
        var candidates = TrackQuery.Candidates("Creep", "Radiohead").ToList();

        Assert.Single(candidates);
    }

    [Fact]
    public void A_track_with_no_title_produces_no_lookups()
    {
        Assert.Empty(TrackQuery.Candidates("   ", "Radiohead"));
    }

    [Fact]
    public void Surrounding_whitespace_never_reaches_the_query()
    {
        var candidate = TrackQuery.Candidates("  Creep  ", "  Radiohead  ").First();

        Assert.Equal("Creep", candidate.Title);
        Assert.Equal("Radiohead", candidate.Artist);
    }
}
