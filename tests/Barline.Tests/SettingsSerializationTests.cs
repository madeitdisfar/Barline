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

    /// <summary>
    /// The case that prompted this: the acrylic background was removed, and every file
    /// naming it would otherwise have failed to parse — taking every unrelated setting
    /// down with it, because one bad value fails the whole document.
    /// </summary>
    [Fact]
    public void A_setting_naming_a_removed_option_does_not_discard_the_rest_of_the_file()
    {
        const string json = """
            {
              "VisualizerBarCount": 6,
              "LyricsEnabled": true,
              "PanelAppearance": {
                "Name": "Mine",
                "FontSize": 28,
                "TextColor": "#FF8800",
                "Background": "Acrylic"
              }
            }
            """;

        var settings = JsonSerializer.Deserialize<WidgetSettings>(json, Options);

        Assert.NotNull(settings);

        // The unknown option falls back...
        Assert.Equal(LyricsBackground.Tinted, settings!.PanelAppearance.Background);

        // ...and nothing else is lost.
        Assert.Equal(6, settings.VisualizerBarCount);
        Assert.True(settings.LyricsEnabled);
        Assert.Equal("Mine", settings.PanelAppearance.Name);
        Assert.Equal(28d, settings.PanelAppearance.FontSize);
        Assert.Equal("#FF8800", settings.PanelAppearance.TextColor);
    }

    [Fact]
    public void Enums_are_written_by_name_so_the_file_stays_readable()
    {
        var settings = new WidgetSettings
        {
            LyricsPosition = LyricsPanelPosition.TopCenter,
            LyricsHover = LyricsHoverBehavior.Hide,
        };

        string json = JsonSerializer.Serialize(settings, Options);

        Assert.Contains("\"TopCenter\"", json);
        Assert.Contains("\"Hide\"", json);
    }

    [Fact]
    public void A_known_option_still_round_trips()
    {
        var settings = new WidgetSettings();
        settings.PanelAppearance.Background = LyricsBackground.Solid;
        settings.PanelAppearance.Effect = LyricsEffect.Glow;

        var restored = JsonSerializer.Deserialize<WidgetSettings>(
            JsonSerializer.Serialize(settings, Options), Options);

        Assert.Equal(LyricsBackground.Solid, restored!.PanelAppearance.Background);
        Assert.Equal(LyricsEffect.Glow, restored.PanelAppearance.Effect);
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
        settings.PanelAppearance.FontSize = 900d;
        settings.PanelAppearance.BackgroundOpacity = 4d;
        settings.LyricsCustomX = -30d;

        settings.Normalize();

        Assert.Equal(LyricsAppearance.MaxFontSize, settings.PanelAppearance.FontSize);
        Assert.Equal(1d, settings.PanelAppearance.BackgroundOpacity);
        Assert.Equal(0d, settings.LyricsCustomX);
    }
}
