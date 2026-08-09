using System.Text.Json;
using Barline.Lyrics;
using Barline.Settings;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// The rules that make a preset mean one thing.
/// </summary>
/// <remarks>
/// A style now says where the lyrics go as well as how they look, which is what stopped
/// the same preset describing two different designs. The cost is that a preset written
/// before that says nothing about placement, and has to be recognized rather than read
/// as one that chose the defaults.
/// </remarks>
public class LyricsStyleTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new TolerantEnumConverterFactory() },
    };

    /// <summary>
    /// The load-bearing detail behind the whole migration: the serializer leaves an
    /// absent key at whatever the object was constructed with, so a Schema that
    /// defaulted to the current version would make every old file claim to be current.
    /// </summary>
    [Fact]
    public void A_preset_that_never_mentioned_a_schema_reads_as_older_than_current()
    {
        var preset = JsonSerializer.Deserialize<LyricsAppearance>(
            """{ "Name": "Mine", "FontSize": 22 }""", Options);

        Assert.NotNull(preset);
        Assert.True(preset!.Schema < LyricsAppearance.CurrentSchema);
    }

    [Fact]
    public void A_style_written_now_says_which_schema_it_is()
    {
        var written = JsonSerializer.Deserialize<LyricsAppearance>(
            JsonSerializer.Serialize(
                new LyricsAppearance { Schema = LyricsAppearance.CurrentSchema }, Options),
            Options);

        Assert.Equal(LyricsAppearance.CurrentSchema, written!.Schema);
    }

    /// <summary>
    /// Loading a preset that predates placement must leave the lyrics where they are.
    /// Asserting a default it never chose would move them somewhere the file never asked
    /// for, which is worse than not applying anything.
    /// </summary>
    [Fact]
    public void Taking_placement_leaves_the_look_alone()
    {
        var current = new LyricsAppearance
        {
            Display = LyricsDisplayMode.Panel,
            Position = LyricsPanelPosition.TopCenter,
            CustomX = 20d,
            CustomY = 30d,
            PanelWidth = 700,
            PanelHeight = 140,
        };

        var loaded = new LyricsAppearance { Name = "Old", FontSize = 33d, TextColor = "#123456" };
        loaded.TakePlacementFrom(current);

        Assert.Equal(LyricsDisplayMode.Panel, loaded.Display);
        Assert.Equal(LyricsPanelPosition.TopCenter, loaded.Position);
        Assert.Equal(20d, loaded.CustomX);
        Assert.Equal(700, loaded.PanelWidth);
        Assert.Equal(140, loaded.PanelHeight);

        // ...and the look it was loaded for is untouched.
        Assert.Equal("Old", loaded.Name);
        Assert.Equal(33d, loaded.FontSize);
        Assert.Equal("#123456", loaded.TextColor);
    }

    /// <summary>
    /// Every built-in has to be reachable from every other one. Before placement was
    /// part of a preset they all described the panel, and a set where every choice is a
    /// one-way trip out of the widget would be a trap rather than a menu.
    /// </summary>
    [Fact]
    public void The_built_ins_cover_both_places_lyrics_can_go()
    {
        Assert.Contains(LyricsAppearance.BuiltIn, p => p.Display == LyricsDisplayMode.Inline);
        Assert.Contains(LyricsAppearance.BuiltIn, p => p.Display == LyricsDisplayMode.Panel);
    }

    /// <summary>
    /// The widget paints no background of its own — the taskbar's material showing
    /// through is the single decision the whole thing is built around — so a built-in
    /// that puts lyrics there must not ask for one.
    /// </summary>
    [Fact]
    public void A_built_in_for_the_widget_asks_for_no_surface()
    {
        foreach (var preset in LyricsAppearance.BuiltIn.Where(p => p.Display == LyricsDisplayMode.Inline))
            Assert.Equal(LyricsBackground.None, preset.Background);
    }

    /// <summary>
    /// Every built-in has to survive a round trip through its own file, since that is
    /// how all of them are actually loaded — they are written to disk on first run
    /// rather than compiled in and used directly.
    /// </summary>
    [Fact]
    public void Every_built_in_round_trips_through_its_file()
    {
        foreach (var preset in LyricsAppearance.BuiltIn)
        {
            var restored = JsonSerializer.Deserialize<LyricsAppearance>(
                JsonSerializer.Serialize(preset, Options), Options);

            Assert.NotNull(restored);
            Assert.Equal(preset.Display, restored!.Display);
            Assert.Equal(preset.FontFamily, restored.FontFamily);
            Assert.Equal(preset.Background, restored.Background);
            Assert.Equal(preset.Effect, restored.Effect);
        }
    }
}
