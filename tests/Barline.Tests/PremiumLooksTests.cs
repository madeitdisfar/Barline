using System.Linq;
using Barline.Lyrics;
using Barline.Platform;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Which looks the free build is allowed to draw, and how that is decided.
/// </summary>
/// <remarks>
/// Decided by content rather than by name throughout, because built-ins and saved
/// presets are the same kind of file in the same folder and a file written while
/// licensed outlives the license. Anything that keyed off the name would let a rename
/// carry a paid look into a free build.
/// </remarks>
public class PremiumLooksTests
{
    [Fact]
    public void A_plain_look_is_free()
    {
        Assert.False(new LyricsAppearance().UsesPremium);
    }

    [Fact]
    public void A_glow_is_paid()
    {
        Assert.True(new LyricsAppearance { Effect = LyricsEffect.Glow }.UsesPremium);
    }

    [Fact]
    public void A_freely_placed_panel_is_paid()
    {
        Assert.True(
            new LyricsAppearance { Position = LyricsPanelPosition.Custom }.UsesPremium);
    }

    /// <summary>
    /// The fixed positions are not the paid one. Only placing it by hand is.
    /// </summary>
    /// <remarks>
    /// Not a theory taking the position, because the enum is internal and an xUnit test
    /// method has to be public. Listed rather than looped so that adding a position
    /// makes this fail to compile instead of quietly not covering it.
    /// </remarks>
    [Fact]
    public void The_fixed_positions_are_free()
    {
        Assert.False(
            new LyricsAppearance { Position = LyricsPanelPosition.AboveWidget }.UsesPremium);
        Assert.False(
            new LyricsAppearance { Position = LyricsPanelPosition.BottomCenter }.UsesPremium);
        Assert.False(
            new LyricsAppearance { Position = LyricsPanelPosition.TopCenter }.UsesPremium);
    }

    /// <summary>
    /// The floor the free build has to look finished at. If this ever drops to one, the
    /// free window is a single look and the screenshots stop selling anything.
    /// </summary>
    [Fact]
    public void The_free_build_ships_more_than_one_look()
    {
        var free = LyricsAppearance.BuiltIn.Where(preset => !preset.UsesPremium).ToList();

        Assert.True(free.Count >= 3, $"only {free.Count} free built-ins");

        // And at least one of each display, or a whole mode is paid by accident.
        Assert.Contains(free, preset => preset.Display == LyricsDisplayMode.Inline);
        Assert.Contains(free, preset => preset.Display == LyricsDisplayMode.Panel);
    }

    /// <summary>
    /// The plainest look must never be the paid one: it is what a fresh install starts
    /// on, so gating it would lock somebody out of their own default.
    /// </summary>
    [Fact]
    public void The_shipped_default_is_free()
    {
        Assert.False(new LyricsAppearance { Name = "Widget" }.UsesPremium);
    }

    [Fact]
    public void A_retired_look_still_matches_the_copy_we_shipped()
    {
        foreach (var retired in LyricsAppearance.Retired)
            Assert.True(retired.LooksLike(retired.Clone()));
    }

    /// <summary>
    /// The narrowness of the deletion is the whole safety of it: one edited field and
    /// the file stops being ours to remove.
    /// </summary>
    [Fact]
    public void An_edited_copy_of_a_retired_look_is_no_longer_ours()
    {
        var retired = LyricsAppearance.Retired[0];
        var edited = retired.Clone();

        edited.FontSize += 1d;

        Assert.False(edited.LooksLike(retired));
    }

    /// <summary>
    /// The name is not part of the comparison, so renaming a built-in does not save it
    /// from retirement — only changing how it looks does.
    /// </summary>
    [Fact]
    public void Renaming_a_retired_look_does_not_change_what_it_is()
    {
        var retired = LyricsAppearance.Retired[0];
        var renamed = retired.Clone();

        renamed.Name = "Mine";

        Assert.True(renamed.LooksLike(retired));
    }

    /// <summary>
    /// Unpackaged builds are the source, and the source is GPL-3.0. Gating one would
    /// only inconvenience the people the license is written for.
    /// </summary>
    [Fact]
    public void A_build_from_source_is_licensed()
    {
        Assert.False(PackageContext.IsPackaged);
        Assert.True(new LicenseService().Premium);
    }

    /// <summary>
    /// Long enough that it never expires in practice for someone who has been confirmed
    /// once. Shortening it cannot help — a refund arrives as a positive no, which
    /// deletes the memory outright — so it would only ever cost a paying customer.
    /// </summary>
    [Fact]
    public void The_remembered_yes_lasts_a_long_time()
    {
        Assert.True(LicenseService.Grace.TotalDays >= 365);
    }
}
