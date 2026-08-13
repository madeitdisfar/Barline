using System.Text.Json;
using Barline.Lyrics;
using Barline.Settings;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// How the settings file survives the app changing underneath it. Options get renamed
/// and removed, and a user's file should not be collateral damage when they do.
/// </summary>
public class SettingsSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new TolerantEnumConverterFactory() },
    };

    private static WidgetSettings Read(string json)
    {
        var settings = JsonSerializer.Deserialize<WidgetSettings>(json, Options);

        Assert.NotNull(settings);
        settings!.Normalize();

        return settings;
    }

    /// <summary>
    /// What a fresh install looks like before anything has been chosen.
    /// </summary>
    /// <remarks>
    /// Pinned because both of these are first-impression decisions rather than
    /// arbitrary values. The panel is a window of its own, so it is what shows the
    /// styling off, and word timing is estimated for nearly every track, so the line
    /// at a time is the reading that never drifts.
    /// </remarks>
    [Fact]
    public void A_fresh_install_shows_lyrics_in_the_panel_a_line_at_a_time()
    {
        var settings = new WidgetSettings();

        Assert.Equal(LyricsDisplayMode.Panel, settings.LyricsStyle.Display);
        Assert.False(settings.LyricsWordByWord);
    }

    /// <summary>
    /// The default is a copy, not the built-in itself. The settings window edits this
    /// object in place, so sharing the instance would let a first run rewrite the
    /// template every other preset is measured against.
    /// </summary>
    [Fact]
    public void The_default_style_is_not_the_shared_built_in()
    {
        var first = new WidgetSettings().LyricsStyle;
        var second = new WidgetSettings().LyricsStyle;

        Assert.NotSame(first, second);

        first.FontSize = 99d;

        Assert.NotEqual(99d, second.FontSize);
        Assert.NotEqual(
            99d,
            LyricsAppearance.BuiltIn.First(p => p.Name == LyricsAppearance.DefaultName).FontSize);
    }

    /// <summary>
    /// The case that prompted this: the acrylic background was removed, and every file
    /// naming it would otherwise have failed to parse — taking every unrelated setting
    /// down with it, because one bad value fails the whole document.
    /// </summary>
    [Fact]
    public void A_setting_naming_a_removed_option_does_not_discard_the_rest_of_the_file()
    {
        var settings = Read("""
            {
              "VisualizerBarCount": 6,
              "LyricsEnabled": true,
              "LyricsStyle": {
                "Name": "Mine",
                "FontSize": 28,
                "TextColor": "#FF8800",
                "Background": "Acrylic"
              }
            }
            """);

        // The unknown option falls back...
        Assert.Equal(LyricsBackground.Tinted, settings.LyricsStyle.Background);

        // ...and nothing else is lost.
        Assert.Equal(6, settings.VisualizerBarCount);
        Assert.True(settings.LyricsEnabled);
        Assert.Equal("Mine", settings.LyricsStyle.Name);
        Assert.Equal(28d, settings.LyricsStyle.FontSize);
        Assert.Equal("#FF8800", settings.LyricsStyle.TextColor);
    }

    [Fact]
    public void Enums_are_written_by_name_so_the_file_stays_readable()
    {
        var settings = new WidgetSettings { LyricsHover = LyricsHoverBehavior.Hide };
        settings.LyricsStyle.Position = LyricsPanelPosition.TopCenter;

        string json = JsonSerializer.Serialize(settings, Options);

        Assert.Contains("\"TopCenter\"", json);
        Assert.Contains("\"Hide\"", json);
    }

    [Fact]
    public void A_known_option_still_round_trips()
    {
        var settings = new WidgetSettings();
        settings.LyricsStyle.Background = LyricsBackground.Solid;
        settings.LyricsStyle.Effect = LyricsEffect.Glow;

        var restored = Read(JsonSerializer.Serialize(settings, Options));

        Assert.Equal(LyricsBackground.Solid, restored.LyricsStyle.Background);
        Assert.Equal(LyricsEffect.Glow, restored.LyricsStyle.Effect);
    }

    /// <summary>
    /// Corner radius applies to every background now that the one which could not be
    /// rounded is gone.
    /// </summary>
    [Fact]
    public void Every_background_on_offer_can_be_rounded()
    {
        Assert.DoesNotContain(
            Enum.GetNames<LyricsBackground>(),
            name => name.Equals("Acrylic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_hand_edited_appearance_is_pulled_back_into_range()
    {
        var settings = new WidgetSettings();
        settings.LyricsStyle.FontSize = 900d;
        settings.LyricsStyle.BackgroundOpacity = 4d;
        settings.LyricsStyle.CustomX = -30d;
        settings.LyricsStyle.PanelWidth = 9000;

        settings.Normalize();

        Assert.Equal(LyricsAppearance.MaxFontSize, settings.LyricsStyle.FontSize);
        Assert.Equal(1d, settings.LyricsStyle.BackgroundOpacity);
        Assert.Equal(0d, settings.LyricsStyle.CustomX);
        Assert.Equal(LyricsAppearance.MaxPanelWidth, settings.LyricsStyle.PanelWidth);
    }

    // ---- Version 1 ---------------------------------------------------------

    /// <summary>
    /// A file from before the lyric style became one object. It carried two appearances
    /// and kept placement outside both; the whole point of the change is that there is
    /// now one, so the migration has to pick the one that was actually in use and fold
    /// the placement into it.
    /// </summary>
    [Fact]
    public void A_version_1_file_keeps_the_look_that_was_in_use()
    {
        var settings = Read("""
            {
              "Version": 1,
              "LyricsEnabled": true,
              "LyricsDisplay": "Panel",
              "LyricsPosition": "TopCenter",
              "LyricsCustomX": 25,
              "LyricsCustomY": 80,
              "LyricsPanelWidth": 640,
              "LyricsPanelHeight": 120,
              "PanelAppearance": { "Name": "Mine", "FontSize": 26, "TextColor": "#00FF99" },
              "InlineAppearance": { "Name": "Other", "FontSize": 11 }
            }
            """);

        Assert.Equal("Mine", settings.LyricsStyle.Name);
        Assert.Equal(26d, settings.LyricsStyle.FontSize);
        Assert.Equal("#00FF99", settings.LyricsStyle.TextColor);

        // Placement lived outside the appearance and now belongs to it.
        Assert.Equal(LyricsDisplayMode.Panel, settings.LyricsStyle.Display);
        Assert.Equal(LyricsPanelPosition.TopCenter, settings.LyricsStyle.Position);
        Assert.Equal(25d, settings.LyricsStyle.CustomX);
        Assert.Equal(80d, settings.LyricsStyle.CustomY);
        Assert.Equal(640, settings.LyricsStyle.PanelWidth);
        Assert.Equal(120, settings.LyricsStyle.PanelHeight);
    }

    /// <summary>The other half: whichever mode was in use is the one that survives.</summary>
    [Fact]
    public void A_version_1_file_in_widget_mode_keeps_the_inline_look()
    {
        var settings = Read("""
            {
              "Version": 1,
              "LyricsDisplay": "Inline",
              "PanelAppearance": { "Name": "Panelly", "FontSize": 26 },
              "InlineAppearance": { "Name": "Inliney", "FontSize": 11 }
            }
            """);

        Assert.Equal("Inliney", settings.LyricsStyle.Name);
        Assert.Equal(11d, settings.LyricsStyle.FontSize);
        Assert.Equal(LyricsDisplayMode.Inline, settings.LyricsStyle.Display);
    }

    /// <summary>
    /// Once folded in, the old keys have to leave. Writing them back would leave dead
    /// settings in the file that read as live ones, and the migration would then run
    /// again on every load and undo whatever had been changed since.
    /// </summary>
    [Fact]
    public void Migrating_clears_the_old_keys_out_of_the_file()
    {
        var settings = Read("""
            {
              "Version": 1,
              "LyricsDisplay": "Panel",
              "PanelAppearance": { "Name": "Mine" }
            }
            """);

        string json = JsonSerializer.Serialize(settings, Options);

        Assert.DoesNotContain("PanelAppearance", json);
        Assert.DoesNotContain("InlineAppearance", json);
        Assert.DoesNotContain("LyricsDisplay", json);
        Assert.DoesNotContain("LyricsPanelWidth", json);
        Assert.Equal(WidgetSettings.CurrentVersion, settings.Version);
    }

    /// <summary>A file that never had the old keys is left exactly as it is.</summary>
    [Fact]
    public void A_current_file_is_not_migrated()
    {
        var settings = Read("""
            {
              "Version": 2,
              "LyricsStyle": { "Name": "Mine", "Display": "Panel", "PanelWidth": 700 }
            }
            """);

        Assert.Equal("Mine", settings.LyricsStyle.Name);
        Assert.Equal(700, settings.LyricsStyle.PanelWidth);
    }
}
