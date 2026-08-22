using Barline.Platform;
using Barline.Shell;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Which end of the taskbar the widget takes.
/// </summary>
/// <remarks>
/// The left end is free on the centered taskbar Windows 11 ships with, and is Start
/// and the task buttons the moment somebody aligns the taskbar left. Verifying the
/// crossing on a running desktop would mean writing a Windows setting that belongs to
/// whoever is at the machine, and the interesting cases are the ones a desk cannot
/// easily be put into: no tray to measure, a tray too close to the left end, a taskbar
/// on a second display. The arithmetic is separated from the probe so all of it can be
/// checked here.
/// </remarks>
public class WidgetSlotTests
{
    /// <summary>A 2880-wide taskbar, with its tray starting where this one's does.</summary>
    private static TaskbarState State(bool leftAligned, int? trayLeft, int left = 0) =>
        new(
            IsAvailable: true,
            ShouldShow: true,
            Rect: new NativeMethods.RECT
            {
                Left = left,
                Top = 1704,
                Right = left + 2880,
                Bottom = 1800,
            },
            Dpi: 192,
            IsAutoHide: false,
            LeftAligned: leftAligned,
            TrayLeft: trayLeft);

    [Fact]
    public void A_centered_taskbar_leaves_the_widget_at_the_left_end()
    {
        Assert.Equal(0, State(leftAligned: false, trayLeft: 2396).WidgetLeft(600));
    }

    [Fact]
    public void A_left_aligned_taskbar_parks_the_widget_against_the_tray()
    {
        Assert.Equal(1796, State(leftAligned: true, trayLeft: 2396).WidgetLeft(600));
    }

    [Fact]
    public void A_tray_that_could_not_be_found_leaves_the_widget_where_it_was()
    {
        // Every existing user is on a centered taskbar with the widget at the left end.
        // An unknown tray edge is a reason to change nothing, not a reason to guess.
        Assert.Equal(0, State(leftAligned: true, trayLeft: null).WidgetLeft(600));
    }

    [Fact]
    public void A_taskbar_too_narrow_for_both_keeps_the_widget_on_it()
    {
        // Rather than off the left side of the screen, which is where subtracting an
        // unclamped width from a near tray edge puts it.
        Assert.Equal(0, State(leftAligned: true, trayLeft: 400).WidgetLeft(600));
    }

    [Fact]
    public void The_far_end_is_measured_on_the_taskbar_that_is_being_used()
    {
        // A second display starts at 2880, so a tray edge is an absolute coordinate
        // rather than an offset into the taskbar.
        var state = State(leftAligned: true, trayLeft: 5276, left: 2880);

        Assert.Equal(4676, state.WidgetLeft(600));
    }

    [Fact]
    public void A_second_display_still_uses_its_own_left_end_when_centered()
    {
        Assert.Equal(2880, State(leftAligned: false, trayLeft: 5276, left: 2880).WidgetLeft(600));
    }

    [Fact]
    public void The_far_end_is_where_the_lyrics_panel_is_told_to_go()
    {
        Assert.True(State(leftAligned: true, trayLeft: 2396).WidgetAtFarEnd(600));
    }

    [Fact]
    public void A_widget_that_did_not_cross_leaves_the_panel_where_it_was()
    {
        // Both ways of not crossing: nothing to measure, and no room to cross into.
        Assert.False(State(leftAligned: true, trayLeft: null).WidgetAtFarEnd(600));
        Assert.False(State(leftAligned: true, trayLeft: 400).WidgetAtFarEnd(600));
        Assert.False(State(leftAligned: false, trayLeft: 2396).WidgetAtFarEnd(600));
    }

    [Fact]
    public void Crossing_is_judged_on_the_taskbar_being_used()
    {
        // Not against zero, which is another display's left edge.
        Assert.True(State(leftAligned: true, trayLeft: 5276, left: 2880).WidgetAtFarEnd(600));
        Assert.False(State(leftAligned: false, trayLeft: 5276, left: 2880).WidgetAtFarEnd(600));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void The_alignment_value_is_read_as_Windows_writes_it(int raw, bool left)
    {
        Assert.Equal(left, TaskbarAlignment.Interpret(raw));
    }

    [Fact]
    public void An_untouched_install_counts_as_centered()
    {
        // Windows 11 centers by default and writes the value only once it is changed.
        Assert.False(TaskbarAlignment.Interpret(null));
    }

    [Fact]
    public void A_value_of_another_shape_counts_as_centered_too()
    {
        Assert.False(TaskbarAlignment.Interpret("0"));
    }
}
