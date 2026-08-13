using System.IO;
using Barline.Lyrics;
using Barline.Settings;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// What happens to a paying customer's settings when the license cannot be confirmed.
/// </summary>
/// <remarks>
/// The one part of the licensing that can destroy something. Everything else it does is
/// recoverable by launching again with a better answer, but a value taken out of
/// <c>settings.json</c> and not written down anywhere is gone, and the person most
/// likely to hit it is somebody who paid and whose Store had a bad morning.
/// </remarks>
public class PremiumSettingsTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"barline-backup-{Guid.NewGuid():N}.json");

    /// <summary>Everything paid, set at once.</summary>
    private static WidgetSettings Loaded() => new()
    {
        VisualizerBarCount = WidgetSettings.MaxBarCount,
        VisualizerColor = VisualizerColorMode.AlbumArt,
        LyricsStyle = new LyricsAppearance
        {
            Effect = LyricsEffect.Glow,
            EffectRadius = 22d,
            Position = LyricsPanelPosition.Custom,
            CustomX = 12d,
            CustomY = 88d,
        },
    };

    [Fact]
    public void Stripping_removes_every_paid_value()
    {
        string backup = TempFile();
        var settings = Loaded();

        try
        {
            Assert.True(PremiumSettings.Strip(settings, backup));

            Assert.Equal(WidgetSettings.MinBarCount, settings.VisualizerBarCount);
            Assert.Equal(VisualizerColorMode.Default, settings.VisualizerColor);
            Assert.Equal(LyricsEffect.None, settings.LyricsStyle.Effect);
            Assert.Equal(LyricsPanelPosition.AboveWidget, settings.LyricsStyle.Position);
        }
        finally { File.Delete(backup); }
    }

    /// <summary>
    /// The property the whole design rests on: nothing paid is lost by being stripped,
    /// only moved, so a wrong answer costs one session rather than a configuration.
    /// </summary>
    [Fact]
    public void What_is_stripped_comes_back_exactly()
    {
        string backup = TempFile();
        var settings = Loaded();

        try
        {
            PremiumSettings.Strip(settings, backup);

            Assert.True(PremiumSettings.Restore(settings, backup));

            Assert.Equal(WidgetSettings.MaxBarCount, settings.VisualizerBarCount);
            Assert.Equal(VisualizerColorMode.AlbumArt, settings.VisualizerColor);
            Assert.Equal(LyricsEffect.Glow, settings.LyricsStyle.Effect);
            Assert.Equal(22d, settings.LyricsStyle.EffectRadius);
            Assert.Equal(LyricsPanelPosition.Custom, settings.LyricsStyle.Position);
            Assert.Equal(12d, settings.LyricsStyle.CustomX);
            Assert.Equal(88d, settings.LyricsStyle.CustomY);
        }
        finally { File.Delete(backup); }
    }

    /// <summary>
    /// A restore must not undo a decision made after the strip. Somebody who spent a
    /// week unlicensed and picked a color in the meantime has said something newer than
    /// the backup holds.
    /// </summary>
    [Fact]
    public void A_newer_choice_survives_the_restore()
    {
        string backup = TempFile();
        var settings = Loaded();

        try
        {
            PremiumSettings.Strip(settings, backup);

            // Made while unlicensed, so it is not the fallback the strip left behind.
            settings.VisualizerColor = VisualizerColorMode.SystemAccent;

            PremiumSettings.Restore(settings, backup);

            Assert.Equal(VisualizerColorMode.SystemAccent, settings.VisualizerColor);

            // The untouched ones still come back.
            Assert.Equal(LyricsEffect.Glow, settings.LyricsStyle.Effect);
        }
        finally { File.Delete(backup); }
    }

    [Fact]
    public void A_restore_clears_the_backup()
    {
        string backup = TempFile();
        var settings = Loaded();

        try
        {
            PremiumSettings.Strip(settings, backup);
            Assert.True(File.Exists(backup));

            PremiumSettings.Restore(settings, backup);
            Assert.False(File.Exists(backup));
        }
        finally { if (File.Exists(backup)) File.Delete(backup); }
    }

    /// <summary>
    /// Stripping twice must not overwrite the first backup with the fallbacks the first
    /// strip just wrote, which would quietly turn a recoverable state into a lost one.
    /// </summary>
    [Fact]
    public void Stripping_an_already_stripped_file_keeps_the_original_values()
    {
        string backup = TempFile();
        var settings = Loaded();

        try
        {
            PremiumSettings.Strip(settings, backup);

            Assert.False(PremiumSettings.Strip(settings, backup));

            PremiumSettings.Restore(settings, backup);

            Assert.Equal(WidgetSettings.MaxBarCount, settings.VisualizerBarCount);
            Assert.Equal(LyricsEffect.Glow, settings.LyricsStyle.Effect);
        }
        finally { if (File.Exists(backup)) File.Delete(backup); }
    }

    [Fact]
    public void Nothing_paid_means_nothing_written()
    {
        string backup = TempFile();
        var settings = new WidgetSettings();

        Assert.False(PremiumSettings.Strip(settings, backup));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void A_restore_with_no_backup_changes_nothing()
    {
        var settings = new WidgetSettings();

        Assert.False(PremiumSettings.Restore(settings, TempFile()));
    }

    /// <summary>
    /// A fresh install asks for nothing paid, which is what lets the guard in
    /// <see cref="SettingsStore.Update"/> treat "something is paid now" as "this change
    /// introduced it".
    /// </summary>
    [Fact]
    public void Nothing_is_paid_by_default()
    {
        Assert.False(new WidgetSettings().UsesPremium);
    }

    [Fact]
    public void Each_paid_value_is_seen_on_its_own()
    {
        Assert.True(new WidgetSettings
        {
            VisualizerBarCount = WidgetSettings.MaxBarCount,
        }.UsesPremium);

        Assert.True(new WidgetSettings
        {
            VisualizerColor = VisualizerColorMode.AlbumArt,
        }.UsesPremium);

        Assert.True(new WidgetSettings
        {
            LyricsStyle = new LyricsAppearance { Effect = LyricsEffect.Glow },
        }.UsesPremium);

        Assert.True(new WidgetSettings
        {
            LyricsStyle = new LyricsAppearance { Position = LyricsPanelPosition.Custom },
        }.UsesPremium);
    }

    /// <summary>
    /// The property the guard rests on. It fires only when nothing was paid a moment
    /// ago, so whatever Strip then removes can only be what the change just added, and
    /// the settings come back to a state that asks for nothing paid.
    /// </summary>
    [Fact]
    public void Stripping_what_a_change_introduced_restores_the_floor()
    {
        string backup = TempFile();
        var settings = new WidgetSettings();

        try
        {
            Assert.False(settings.UsesPremium);

            // Stand in for a path that was never gated.
            settings.LyricsStyle.Effect = LyricsEffect.Glow;
            settings.VisualizerBarCount = WidgetSettings.MaxBarCount;

            Assert.True(settings.UsesPremium);

            Assert.True(PremiumSettings.Strip(settings, backup));
            Assert.False(settings.UsesPremium);

            // And nothing was destroyed on the way: buying puts it back.
            Assert.True(PremiumSettings.Restore(settings, backup));
            Assert.Equal(LyricsEffect.Glow, settings.LyricsStyle.Effect);
            Assert.Equal(WidgetSettings.MaxBarCount, settings.VisualizerBarCount);
        }
        finally { if (File.Exists(backup)) File.Delete(backup); }
    }

    /// <summary>
    /// Free settings must not look paid, or the guard would strip a legitimate change.
    /// </summary>
    [Fact]
    public void The_free_settings_are_not_mistaken_for_paid()
    {
        Assert.False(new WidgetSettings
        {
            VisualizerBarCount = WidgetSettings.MinBarCount,
            VisualizerColor = VisualizerColorMode.Custom,
            VisualizerEnabled = false,
            LyricsEnabled = true,
            LyricsHover = LyricsHoverBehavior.Hide,
            LyricsWordByWord = true,
            LyricsStyle = new LyricsAppearance
            {
                Display = LyricsDisplayMode.Panel,
                Position = LyricsPanelPosition.TopCenter,
                Italic = true,
                Lowercase = true,
                FontSize = 24d,
                Background = LyricsBackground.Solid,
            },
        }.UsesPremium);
    }
}
