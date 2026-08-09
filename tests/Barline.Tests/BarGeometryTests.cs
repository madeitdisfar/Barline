using Barline.Settings;
using Barline.Ui;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// The sizing rule behind the bar-count setting: more bars must mean more detail and
/// nothing else. The widget sits in a fixed zone on the taskbar, so a count that
/// widened it would reflow the layout, and one that added ink would quietly make the
/// visualizer heavier than the color was corrected for.
/// </summary>
public class BarGeometryTests
{
    public static TheoryData<int> SupportedCounts
    {
        get
        {
            var data = new TheoryData<int>();
            for (int count = WidgetSettings.MinBarCount; count <= WidgetSettings.MaxBarCount; count++)
                data.Add(count);
            return data;
        }
    }

    /// <summary>
    /// The count the design was drawn at. Pinned exactly, so generalizing the rule
    /// cannot quietly restyle the default.
    /// </summary>
    [Fact]
    public void The_default_count_keeps_the_original_three_pixel_bars()
    {
        var (width, gap) = Visualizer.BarGeometry(WidgetSettings.DefaultBarCount);

        Assert.Equal(3d, width, precision: 10);
        Assert.Equal(3d, gap, precision: 10);
    }

    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void Every_count_occupies_the_same_width(int count)
    {
        var (width, gap) = Visualizer.BarGeometry(count);

        double occupied = (count * width) + ((count - 1) * gap);

        Assert.Equal(Visualizer.TotalWidth, occupied, precision: 10);
    }

    /// <summary>
    /// The half of the rule that is easy to forget. Holding only the width would stop
    /// the widget stretching but let a higher count paint more ink, so "detailed"
    /// would also read as "heavier" against the taskbar.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void Every_count_paints_the_same_amount_of_ink(int count)
    {
        var (width, _) = Visualizer.BarGeometry(count);

        double defaultInk = WidgetSettings.DefaultBarCount
            * Visualizer.BarGeometry(WidgetSettings.DefaultBarCount).Width;

        Assert.Equal(defaultInk, count * width, precision: 10);
    }

    [Fact]
    public void More_bars_means_thinner_bars_and_tighter_gaps()
    {
        for (int count = WidgetSettings.MinBarCount; count < WidgetSettings.MaxBarCount; count++)
        {
            var current = Visualizer.BarGeometry(count);
            var next = Visualizer.BarGeometry(count + 1);

            Assert.True(next.Width < current.Width, $"{count + 1} bars are not thinner than {count}");
            Assert.True(next.Gap < current.Gap, $"{count + 1} bars do not sit tighter than {count}");
        }
    }

    /// <summary>
    /// The thinness floor the range was chosen around. Below two logical pixels the
    /// bars stop surviving antialiasing at 100% scaling: neighboring tall bars merge
    /// into a block and the gaps gray out, which is worse on the light taskbar where
    /// the bar color is translucent as well as thin.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void No_supported_count_draws_a_bar_under_two_pixels(int count)
    {
        var (width, _) = Visualizer.BarGeometry(count);

        Assert.True(width >= 2d, $"{count} bars would be {width:F2}px wide");
    }

    /// <summary>
    /// The settings window draws the range as three named segments — Simple, Balanced,
    /// Detailed. Widening the range without adding a segment would leave a value the
    /// user can reach by hand-editing the file but not through the UI.
    /// </summary>
    [Fact]
    public void The_range_matches_the_segments_the_settings_window_offers()
    {
        int offered = WidgetSettings.MaxBarCount - WidgetSettings.MinBarCount + 1;

        Assert.Equal(3, offered);
    }

    [Fact]
    public void An_out_of_range_hand_edit_is_pulled_back_rather_than_rejected()
    {
        var settings = new WidgetSettings
        {
            VisualizerBarCount = 400,
            VisualizerColor = VisualizerColorMode.AlbumArt,
        };

        settings.Normalize();

        Assert.Equal(WidgetSettings.MaxBarCount, settings.VisualizerBarCount);

        // The rest of the file survives a bad value in one field.
        Assert.Equal(VisualizerColorMode.AlbumArt, settings.VisualizerColor);
    }
}
