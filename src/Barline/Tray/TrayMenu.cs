using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Barline.Settings;
using Barline.Ui;

using Win32 = Barline.Shell.NativeMethods;

namespace Barline.Tray;

/// <summary>
/// The notification area's menu, as a WPF flyout.
/// </summary>
/// <remarks>
/// <para>
/// WPF rather than the WinForms <c>ContextMenuStrip</c> this replaces, because that
/// control's DPI model never agreed with the app around it. It took its scale from a
/// field the framework updates only after the window has moved, so a menu opening on a
/// second display was laid out for the one it was last on; it rescaled any font
/// assigned to it by the ratio between its window's creation DPI and the current one;
/// it sized items wider than the menu holding them; and it computed its image gutter
/// once, at creation, and scaled it again per display. Each of those had a workaround.
/// Together they were an argument for not using the control.
/// </para>
/// <para>
/// Nothing here multiplies by a DPI scale, and that is the point. The app declares
/// PerMonitorV2 in its manifest and WPF honors it, so the sizes in the XAML are logical
/// units and a display at another scale is not a special case to compensate for. The
/// only physical pixels left are the cursor position <see cref="Show"/> is handed,
/// which goes straight to <c>SetWindowPos</c> without being converted at all.
/// </para>
/// </remarks>
internal sealed class TrayMenu : IDisposable
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Theme _theme;
    private readonly ResourceDictionary _styles;

    /// <summary>
    /// A one-pixel invisible window the flyout hangs from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists to carry a DPI context. A <c>ContextMenu</c> with no placement target
    /// has no visual parent, and WPF then gives its popup the primary display's scale
    /// wherever the popup actually appears: measured, a menu opened on a 150% second
    /// display came out at the 200% primary's pixel size, which is the same complaint
    /// the WinForms menu drew. Hung from a window moved onto the target display first,
    /// the popup inherits that window's scale and is laid out for the display it is on.
    /// </para>
    /// <para>
    /// It doubles as the activation Win32 wants. A popup owned by no active window
    /// never receives the activation that lets it close again when the next click lands
    /// elsewhere, so it would sit on the desktop until something was chosen.
    /// </para>
    /// </remarks>
    private readonly Window _anchor;

    /// <summary>
    /// Whether the visualizer is on, which is what the checkmark shows.
    /// </summary>
    /// <remarks>
    /// Kept here rather than read off a menu item, because the menu is built fresh for
    /// each opening and so has no state of its own to be the truth.
    /// </remarks>
    private bool _visualizerChecked;

    private ContextMenu? _open;

    /// <summary>Whether the flyout is on screen.</summary>
    /// <remarks>
    /// Read by the widget, which stops climbing back to the top of the z-order for as
    /// long as this is true. See <c>OverlayWindow.YieldTo</c>.
    /// </remarks>
    public bool IsOpen => _open is not null;

    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? VisualizerToggled;
    public event EventHandler? RestartVisualizerRequested;
    public event EventHandler? RestartRequested;
    public event EventHandler? SettingsRequested;

    public TrayMenu(WidgetSettings settings, Theme theme)
    {
        _theme = theme;
        _visualizerChecked = settings.VisualizerEnabled;

        _styles = new ResourceDictionary
        {
            Source = new Uri("/Barline;component/Tray/TrayMenu.xaml", UriKind.Relative),
        };

        _anchor = new Window
        {
            Width = 1,
            Height = 1,
            Opacity = 0d,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
        };
    }

    /// <summary>
    /// Opens the menu at the pointer.
    /// </summary>
    /// <param name="cursor">The cursor position, in physical screen pixels.</param>
    /// <remarks>
    /// The anchor is moved by handle rather than through <c>Left</c> and <c>Top</c>,
    /// which are device-independent units measured against the primary display and so
    /// land somewhere else entirely on a desk with two scales. Physical pixels are what
    /// the cursor arrives in and what <c>SetWindowPos</c> takes, so nothing is converted
    /// at all. This is the last place the tray menu touches a screen coordinate.
    /// </remarks>
    public void Show(System.Drawing.Point cursor)
    {
        Close();

        var spot = ClearOfTheTaskbar(cursor);
        var handle = new WindowInteropHelper(_anchor).EnsureHandle();

        // Moved before it is shown, so the window is already on the target display when
        // it is first composed and never has to change scale afterwards.
        Move(handle, spot);
        _anchor.Show();
        Move(handle, spot);

        SetForegroundWindow(handle);

        _open = Build();
        _open.IsOpen = true;

    }

    private static void Move(IntPtr handle, System.Drawing.Point spot) =>
        Win32.SetWindowPos(
            handle, Win32.HWND_TOPMOST, spot.X, spot.Y, 1, 1, Win32.SWP_NOACTIVATE);

    /// <summary>
    /// Pulls the anchor off the taskbar and onto the desktop proper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both ways of opening this menu put the pointer on the taskbar, so the flyout
    /// would otherwise be anchored there. It has nowhere to go but up, and WPF fits it
    /// to the screen rather than to the working area, so the last item came to rest
    /// underneath the widget: covered, because the widget is topmost, and dead to
    /// clicks, because the widget takes input. Anchored clear of the taskbar, the
    /// flyout's bottom lands on the taskbar's top edge, which is where Windows puts
    /// its own menus.
    /// </para>
    /// <para>
    /// Two passes, because neither alone is enough. The working area covers the
    /// ordinary case, and any edge the taskbar happens to be docked to. A taskbar set
    /// to hide itself reserves no working area at all, though, so on one of those the
    /// first pass changes nothing and the second is what does the work.
    /// </para>
    /// </remarks>
    private static System.Drawing.Point ClearOfTheTaskbar(System.Drawing.Point cursor)
    {
        var point = new Win32.POINT { X = cursor.X, Y = cursor.Y };
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero) return cursor;

        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return cursor;

        var spot = new System.Drawing.Point(
            Math.Clamp(cursor.X, info.rcWork.Left, info.rcWork.Right),
            Math.Clamp(cursor.Y, info.rcWork.Top, info.rcWork.Bottom));

        foreach (var taskbar in Shell.Displays.TaskbarHandles())
        {
            if (!Win32.GetWindowRect(taskbar, out var bounds)) continue;

            var pushed = PushOut(spot, bounds, info.rcMonitor);
            if (pushed != spot) return pushed;
        }

        return spot;
    }

    /// <summary>
    /// Moves a point that is on the taskbar off it, or returns it untouched.
    /// </summary>
    /// <param name="spot">The point, in physical screen pixels.</param>
    /// <param name="taskbar">The taskbar's rectangle.</param>
    /// <param name="monitor">The rectangle of the display both are on.</param>
    /// <remarks>
    /// Out across the taskbar rather than along it, since a taskbar spans its display
    /// and leaving by an end would put the flyout beside the Start button. Which way
    /// across is decided by the display rather than by which edge is nearer: the near
    /// edge of a taskbar along the bottom is the bottom of the screen, and leaving that
    /// way is how the pointer ends up somewhere there is no room at all.
    /// </remarks>
    internal static System.Drawing.Point PushOut(
        System.Drawing.Point spot, Win32.RECT taskbar, Win32.RECT monitor)
    {
        if (spot.X < taskbar.Left || spot.X > taskbar.Right) return spot;
        if (spot.Y < taskbar.Top || spot.Y > taskbar.Bottom) return spot;

        if (taskbar.Width >= taskbar.Height)
        {
            return taskbar.Top <= monitor.Top
                ? spot with { Y = taskbar.Bottom }
                : spot with { Y = taskbar.Top };
        }

        return taskbar.Left <= monitor.Left
            ? spot with { X = taskbar.Right }
            : spot with { X = taskbar.Left };
    }

    /// <summary>
    /// Reflects a visualizer-visibility change made elsewhere (the settings window) so
    /// the menu's checkmark does not go stale.
    /// </summary>
    public void SetVisualizerChecked(bool enabled) => _visualizerChecked = enabled;

    /// <summary>
    /// Builds the flyout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built for each opening rather than kept, which is what makes it land where it
    /// was asked to. A WPF popup parks its window at the origin when it closes and
    /// works out a new position only when something it watches has changed, so a second
    /// right-click without moving the mouse first reopened the menu in the top left
    /// corner of the primary display. Nothing exposes "work the position out again",
    /// and a popup that has never been opened has nothing stale to carry.
    /// </para>
    /// <para>
    /// It costs five items and a style lookup, on a human right-click. The only state
    /// that has to survive is one bool, and building fresh also removes the detaching
    /// and reattaching the checkable item needed to keep from echoing its own updates.
    /// </para>
    /// </remarks>
    private ContextMenu Build()
    {
        var menu = new ContextMenu
        {
            Style = (Style)_styles["FluentContextMenu"],
            PlacementTarget = _anchor,

            // Upward from the pointer, which is what a notification-area menu does.
            // The anchor is a single pixel sitting there, so this puts the flyout's
            // bottom left corner on it with no offset arithmetic of its own. WPF still
            // turns it over on its own if there is no room above.
            Placement = PlacementMode.Top,
        };

        menu.Resources.MergedDictionaries.Add(_styles);
        ApplyTheme(menu);

        menu.Closed += (_, _) =>
        {
            // Only if this is still the menu on screen. Right-clicking the tray while
            // the flyout is up dismisses it and opens another, and the dismissal
            // finishes after the replacement is already hanging from the anchor, so an
            // unguarded handler pulls the anchor out from under the new menu and it
            // vanishes about a tenth of a second after appearing.
            if (!ReferenceEquals(_open, menu)) return;

            _anchor.Hide();
            _open = null;
        };

        var itemStyle = (Style)_styles["FluentMenuItem"];
        var separatorStyle = (Style)_styles["FluentSeparator"];

        // Bold, because it is the default action.
        var settings = Item(
            menu, "Settings", itemStyle, () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        settings.FontWeight = FontWeights.SemiBold;

        var visualizer = Item(menu, "Show visualizer", itemStyle, null);
        visualizer.IsCheckable = true;
        visualizer.IsChecked = _visualizerChecked;
        visualizer.Click += (_, _) =>
        {
            _visualizerChecked = visualizer.IsChecked;
            menu.IsOpen = false;
            VisualizerToggled?.Invoke(this, _visualizerChecked);
        };

        // Manual fallback: the watchdog recovers a stalled capture on its own, but this
        // lets the user force it immediately if the visualizer stops answering audio.
        var restartVisualizer = Item(
            menu, "Restart visualizer", itemStyle,
            () => RestartVisualizerRequested?.Invoke(this, EventArgs.Empty));

        // Grouped with Exit rather than with the visualizer above it, because what it
        // acts on is the app rather than the bars.
        var restart = Item(
            menu, "Restart Barline", itemStyle,
            () => RestartRequested?.Invoke(this, EventArgs.Empty));

        var exit = Item(menu, "Exit", itemStyle, () => ExitRequested?.Invoke(this, EventArgs.Empty));

        menu.Items.Add(settings);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(visualizer);
        menu.Items.Add(restartVisualizer);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(restart);
        menu.Items.Add(exit);

        return menu;
    }

    private static MenuItem Item(ContextMenu menu, string header, Style style, Action? onClick)
    {
        var item = new MenuItem { Header = header, Style = style };

        if (onClick is not null)
        {
            // Closed first, so the action runs against a desktop the menu has already
            // left. Opening the settings window under an open flyout leaves the flyout
            // on top of it until something else takes the click.
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                onClick();
            };
        }

        return item;
    }

    /// <summary>
    /// Puts the theme's colors into the menu's own resources.
    /// </summary>
    /// <remarks>
    /// Done at build time rather than kept in step, since the menu is built for each
    /// opening and so cannot be holding a stale color by the time anyone sees it. The
    /// three menu colors are stored as colors rather than brushes, so they are wrapped
    /// and frozen here; the rest are already brushes and already shared.
    /// </remarks>
    private void ApplyTheme(ContextMenu menu)
    {
        menu.Resources["TextPrimaryBrush"] = _theme.TextPrimary;
        menu.Resources["TextTertiaryBrush"] = _theme.TextTertiary;
        menu.Resources["SubtleHoverBrush"] = _theme.SubtleHover;
        menu.Resources["SubtlePressedBrush"] = _theme.SubtlePressed;
        menu.Resources["MenuBackgroundBrush"] = Frozen(_theme.MenuBackground);
        menu.Resources["MenuBorderBrush"] = Frozen(_theme.MenuBorder);
        menu.Resources["MenuDividerBrush"] = Frozen(_theme.MenuDivider);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Close()
    {
        if (_open is null) return;

        _open.IsOpen = false;
        _open = null;
    }

    public void Dispose()
    {
        Close();
        _anchor.Close();
    }
}
