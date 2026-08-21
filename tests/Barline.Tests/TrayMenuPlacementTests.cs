using System.Drawing;
using Barline.Shell;
using Barline.Tray;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Keeping the tray flyout off the taskbar.
/// </summary>
/// <remarks>
/// Both ways of opening the menu put the pointer on the taskbar, and a flyout anchored
/// there is fitted to the screen rather than to the working area, so its last item came
/// to rest under the widget: covered, because the widget is topmost, and dead to clicks,
/// because the widget takes input.
/// <para>
/// The working area handles that on an ordinary desktop, and is verified by looking at
/// one. This is the other half, which is not: a taskbar set to hide itself reserves no
/// working area, so the point has to be pushed off the taskbar's own rectangle. Reaching
/// that from a running desktop would mean changing a Windows setting that belongs to
/// whoever is at the machine, and a taskbar docked to the left or the top is a second
/// configuration again. The geometry is separated out so all four can be checked here.
/// </para>
/// </remarks>
public class TrayMenuPlacementTests
{
    private static NativeMethods.RECT Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    /// <summary>A 2880x1800 display, which is the one these were measured on.</summary>
    private static NativeMethods.RECT Screen() => Rect(0, 0, 2880, 1800);

    /// <summary>A 2880x1800 display with a 96px taskbar along the bottom.</summary>
    private static NativeMethods.RECT BottomTaskbar() => Rect(0, 1704, 2880, 1800);

    [Fact]
    public void A_point_on_a_bottom_taskbar_is_pushed_up_to_its_top()
    {
        // Right-clicking the widget, which is the case that was reported.
        var pushed = TrayMenu.PushOut(new Point(300, 1750), BottomTaskbar(), Screen());

        Assert.Equal(new Point(300, 1704), pushed);
    }

    [Fact]
    public void The_notification_area_end_is_pushed_up_as_well()
    {
        // Far along the taskbar, where the distance to the right edge is small but
        // still much larger than the distance to the top.
        var pushed = TrayMenu.PushOut(new Point(2820, 1780), BottomTaskbar(), Screen());

        Assert.Equal(new Point(2820, 1704), pushed);
    }

    [Fact]
    public void A_point_above_the_taskbar_is_left_alone()
    {
        var spot = new Point(300, 900);

        Assert.Equal(spot, TrayMenu.PushOut(spot, BottomTaskbar(), Screen()));
    }

    [Fact]
    public void A_point_on_another_display_is_left_alone()
    {
        // The sweep asks every taskbar in turn, so each one has to ignore what is not
        // on it rather than claim it.
        var spot = new Point(3400, 1750);

        Assert.Equal(spot, TrayMenu.PushOut(spot, BottomTaskbar(), Screen()));
    }

    [Fact]
    public void A_top_taskbar_pushes_down()
    {
        var pushed = TrayMenu.PushOut(new Point(300, 40), Rect(0, 0, 2880, 96), Screen());

        Assert.Equal(new Point(300, 96), pushed);
    }

    [Fact]
    public void A_left_taskbar_pushes_right()
    {
        var pushed = TrayMenu.PushOut(new Point(40, 900), Rect(0, 0, 96, 1800), Screen());

        Assert.Equal(new Point(96, 900), pushed);
    }

    [Fact]
    public void A_right_taskbar_pushes_left()
    {
        var pushed = TrayMenu.PushOut(new Point(2840, 900), Rect(2784, 0, 2880, 1800), Screen());

        Assert.Equal(new Point(2784, 900), pushed);
    }

    [Fact]
    public void The_way_out_is_across_the_taskbar_rather_than_along_it()
    {
        // The far corner, where the bottom of the screen is the nearest edge of all.
        // Leaving that way would put the pointer where there is no room for a flyout,
        // which is what a first attempt at this did.
        var pushed = TrayMenu.PushOut(new Point(10, 1794), BottomTaskbar(), Screen());

        Assert.Equal(1704, pushed.Y);
    }
}
