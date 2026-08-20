using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Barline.Diagnostics;
using Barline.Settings;
using Barline.Ui;

// The shell layer's interop, aliased rather than imported: this file declares a few
// P/Invokes of its own for the notification area, and a plain using static would put
// two of some names in scope at once.
using Win32 = Barline.Shell.NativeMethods;

namespace Barline.Tray;

/// <summary>
/// Notification-area icon and menu — the widget's only chrome, since a taskbar
/// overlay has nowhere else to hang a menu or a way to quit.
/// </summary>
/// <remarks>
/// The menu holds quick actions only. Anything that is configuration rather than a
/// one-off action lives in the settings window instead, which also avoids two places
/// showing the same state and drifting out of sync.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND: the radius Windows 11 gives its own menus.</summary>
    private const int DwmwcpRound = 2;

    /// <summary>The menu font's em size in logical pixels, being 9pt at 96 DPI.</summary>
    private const double MenuFontPixels = 12d;

    private readonly Theme _theme;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _visualizerItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly FluentMenuRenderer _renderer;

    /// <summary>Redrawn whenever the theme moves, so it is never the wrong ink.</summary>
    private Icon _icon;

    /// <summary>
    /// Device pixels per logical pixel at the DPI the menu's window was created at.
    /// </summary>
    /// <remarks>
    /// The baseline the font is expressed against. See <see cref="ApplyMetrics"/> for
    /// why the font is the one metric here that is not sized for the target display.
    /// </remarks>
    private double _baseScale = 1d;

    private Font? _menuFont;
    private Font? _defaultItemFont;
    private bool _disposed;

    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? VisualizerToggled;
    public event EventHandler? RestartVisualizerRequested;
    public event EventHandler? RestartRequested;
    public event EventHandler? SettingsRequested;

    public TrayIcon(WidgetSettings settings, Theme theme)
    {
        _theme = theme;
        _renderer = new FluentMenuRenderer(theme);

        // Bold, because it is the default action. The font itself is set in
        // ApplyMetrics, which is the only place that knows what size to make it.
        _settingsItem = new ToolStripMenuItem("Settings");
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _visualizerItem = new ToolStripMenuItem("Show visualizer")
        {
            CheckOnClick = true,
            Checked = settings.VisualizerEnabled,
        };
        // A named handler, not a lambda, so SetVisualizerChecked can detach it.
        _visualizerItem.CheckedChanged += OnVisualizerItemChanged;

        // Manual fallback: the watchdog recovers a stalled capture on its own, but
        // this lets the user force it immediately if the visualizer ever stops
        // responding to audio.
        var restartVisualizerItem = new ToolStripMenuItem("Restart visualizer");
        restartVisualizerItem.Click += (_, _) => RestartVisualizerRequested?.Invoke(this, EventArgs.Empty);

        // Grouped with Exit rather than with the visualizer above it, because what it
        // acts on is the app rather than the bars. Worth having on its own merits, for
        // the same reason any long-running background app offers one, and it is also
        // the only route to the restart path that is reachable at all in a Store build:
        // the one other button that offers it appears once, after a purchase.
        var restartItem = new ToolStripMenuItem("Restart Barline");
        restartItem.Click += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new ContextMenuStrip { Renderer = _renderer };
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_visualizerItem);
        _menu.Items.Add(restartVisualizerItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(restartItem);
        _menu.Items.Add(exitItem);

        // Noted before anything touches Handle, which is what creates the window.
        // Re-read on a recreation as well, since that can happen on another display and
        // moves the baseline the font is measured against.
        _menu.HandleCreated += (_, _) =>
        {
            uint dpi = Win32.GetDpiForWindow(_menu.Handle);
            _baseScale = dpi == 0 ? 1d : dpi / 96d;
        };

        // Both are re-applied per opening rather than set once. The menu's window is
        // created on first show and its DPI is not known before that, and the system
        // theme can change while the app runs.
        _menu.Opened += (_, _) => ApplyWindowChrome();
        _menu.Opening += (_, _) => ApplyMetrics();

        ApplyMetrics();

        _icon = CreateIcon(_theme);
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Barline",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        // Last, because the handler redraws the icon and so needs the notification
        // area to already have one.
        _theme.Changed += OnThemeChanged;
    }

    /// <summary>Opens the menu at the cursor, for right-clicks on the widget itself.</summary>
    public void ShowContextMenu() => _menu.Show(Control.MousePosition);

    /// <summary>
    /// Rounds the menu's corners, which is the window's business rather than the
    /// renderer's.
    /// </summary>
    /// <remarks>
    /// Asked of DWM rather than done by setting a window region. A region is clipped
    /// without antialiasing, so the corners come out visibly stepped, and it would also
    /// have to be rebuilt every time the menu resized. DWM rounds the composited result
    /// at the same radius the shell's own menus use, and the app requires Windows 11, so
    /// the attribute is always understood.
    /// </remarks>
    private void ApplyWindowChrome()
    {
        if (!_menu.IsHandleCreated) return;

        try
        {
            int preference = DwmwcpRound;
            DwmSetWindowAttribute(
                _menu.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            // Cosmetic, and the menu is perfectly usable square.
            DebugLog.Write($"tray menu corners unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Sizes the menu for the display it is about to appear on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every figure written here as a logical pixel is multiplied by
    /// <see cref="MenuScale"/> on the way out, because a padding of 8 set on a control
    /// is 8 physical pixels however the display is scaled. WinForms takes the sizes and
    /// margins exactly as given.
    /// </para>
    /// <para>
    /// The font is the exception, and the reason there are two scales rather than one.
    /// WinForms rescales any font assigned to the menu by the ratio between the DPI its
    /// window was created at and the DPI it is on now, so a font sized for the target
    /// display is scaled toward it a second time and lands short. It is therefore
    /// expressed against <see cref="_baseScale"/> and left alone: measured, a font
    /// assigned as 24 pixels from a 200% baseline arrives as 18 on a 150% display,
    /// which is the right answer by the route the framework already takes.
    /// </para>
    /// <para>
    /// Sized in pixels rather than points for the same reason. A point size is
    /// converted again through whichever DPI GDI measures a point against, which adds
    /// a third conversion to a value that has already had two too many.
    /// </para>
    /// <para>
    /// Re-applied on every opening rather than set once, because the answer changes. A
    /// desk with two displays at different scales gets a different one each time the
    /// menu moves between them.
    /// </para>
    /// </remarks>
    private void ApplyMetrics()
    {
        double pixels = MenuScale();

        _renderer.Scale = pixels;

        DebugLog.Write($"tray menu: scale={pixels:F2} baseline={_baseScale:F2}");

        var font = CreateFont(_baseScale);
        var emphasis = new Font(font, FontStyle.Bold);

        _menu.Font = font;

        // Set on the item rather than left to inherit, and so it has to be rebuilt
        // here: an item font, once assigned, no longer follows the menu's.
        _settingsItem.Font = emphasis;

        // After both are in use, so nothing is drawn with a disposed handle.
        _menuFont?.Dispose();
        _defaultItemFont?.Dispose();
        _menuFont = font;
        _defaultItemFont = emphasis;

        // Only decides where the check is centered. It does not move the text and does
        // not widen the menu, which is why the gap between the two is opened by the
        // renderer instead.
        _menu.ImageScalingSize = new Size(Round(20, pixels), Round(20, pixels));

        foreach (ToolStripItem item in _menu.Items)
        {
            // Above and below the text only. The height this produces is the whole of
            // the item's height, so it is the one figure that decides how tall the menu
            // is, and 6 puts a row at about the 30 logical pixels Windows 11 uses.
            item.Padding = new Padding(0, Round(6, pixels), 0, Round(6, pixels));

            // The left inset only, and by margin rather than padding: the dropdown's
            // layout resets an item's horizontal padding, and the menu's own padding
            // with it, but it does place items at their left margin. A right margin is
            // set nowhere because it changes nothing, the item being sized to the full
            // menu either way. The renderer draws to the menu's edge instead.
            item.Margin = new Padding(Round(4, pixels), 0, 0, 0);
        }
    }

    private static int Round(double value, double scale) => (int)Math.Round(value * scale);

    /// <summary>
    /// Device pixels per logical pixel for the display the menu is about to open on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <see cref="Control.DeviceDpi"/>, which is the bug this replaced.
    /// WinForms updates that from <c>WM_DPICHANGED</c>, which arrives once the window
    /// has moved, and the metrics are wanted before it is shown. On a desk with two
    /// scales the menu was laid out at the display it was last on: a third too large on
    /// the way to a 150% screen, clipping the longest item, and correct on the next
    /// opening, so it was always one behind.
    /// </para>
    /// <para>
    /// The drop-down positions its window before it raises <c>Opening</c>, so by the
    /// time this runs the handle is already on the display it will appear on and can
    /// be asked directly. Before there is a window at all, the cursor answers instead:
    /// the menu is anchored to it whether it was opened from the notification area or
    /// by right-clicking the widget.
    /// </para>
    /// </remarks>
    private double MenuScale()
    {
        // Reading Handle creates the window if it does not exist, which is deliberate:
        // the drop-down can only be asked where it is once there is something to ask,
        // and creating it at startup is what puts it in place before the first opening.
        uint dpi = Win32.GetDpiForWindow(_menu.Handle);

        if (dpi == 0) dpi = CursorDpi();

        return dpi == 0 ? 1d : dpi / 96d;
    }

    private static uint CursorDpi()
    {
        try
        {
            if (!Win32.GetCursorPos(out var cursor)) return 0;

            var monitor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return 0;

            return Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out uint x, out _) == 0
                ? x
                : 0;
        }
        catch (Exception ex)
        {
            // An unscaled menu is survivable; failing to open one is not.
            DebugLog.Write($"cursor dpi unavailable: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// The menu font, at the size the display actually needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Segoe UI Variable Text is what Windows 11 sets menus in, and it is the optical
    /// size cut for body text rather than for headings. It ships with Windows 11 and the
    /// package requires that, but a font is a file and files go missing, so a machine
    /// without it falls back to Segoe UI rather than to whatever GDI substitutes, which
    /// is Microsoft Sans Serif and looks like a fault.
    /// </para>
    /// <para>
    /// The scale here is the menu window's creation DPI rather than the display it is
    /// opening on. See <see cref="ApplyMetrics"/>. Twelve pixels is what nine points
    /// comes to at 96, which is the size Windows 11 sets its own menus in.
    /// </para>
    /// </remarks>
    private static Font CreateFont(double scale)
    {
        float size = (float)(MenuFontPixels * scale);

        try
        {
            var font = new Font("Segoe UI Variable Text", size, GraphicsUnit.Pixel);
            if (font.Name == "Segoe UI Variable Text") return font;
            font.Dispose();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"tray menu font unavailable: {ex.Message}");
        }

        return new Font("Segoe UI", size, GraphicsUnit.Pixel);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // The menu's colors are read from the theme at paint time, so a repaint is the
        // whole of that update. The menu is almost never open when this arrives.
        _menu.Invalidate();

        RefreshIcon();
    }

    /// <summary>
    /// Redraws the notification-area icon in the current theme's ink.
    /// </summary>
    /// <remarks>
    /// The replacement is handed over before the old one is destroyed.
    /// <see cref="Icon.FromHandle"/> does not own its <c>HICON</c>, so releasing it
    /// while it is still the icon on display would leave a gap in the tray.
    /// </remarks>
    private void RefreshIcon()
    {
        var previous = _icon;

        _icon = CreateIcon(_theme);
        _notifyIcon.Icon = _icon;

        IntPtr handle = previous.Handle;
        previous.Dispose();
        DestroyIcon(handle);
    }

    /// <summary>
    /// Reflects a visualizer-visibility change made elsewhere (the settings window)
    /// so the menu's checkmark does not go stale.
    /// </summary>
    public void SetVisualizerChecked(bool enabled)
    {
        if (_visualizerItem.Checked == enabled) return;

        // CheckedChanged would otherwise echo this back out as a user action and
        // bounce between the two surfaces.
        _visualizerItem.CheckedChanged -= OnVisualizerItemChanged;
        _visualizerItem.Checked = enabled;
        _visualizerItem.CheckedChanged += OnVisualizerItemChanged;
    }

    private void OnVisualizerItemChanged(object? sender, EventArgs e) =>
        VisualizerToggled?.Invoke(this, _visualizerItem.Checked);

    /// <summary>
    /// Draws the tray icon rather than shipping an .ico, so it always matches the
    /// widget's own visualizer motif and stays crisp at any tray size.
    /// </summary>
    /// <remarks>
    /// Drawing it also means it can be drawn again. The notification area sits on the
    /// taskbar and follows the system theme, so the white bars this always drew were
    /// invisible against a light one, and a shipped .ico would have had to be two files
    /// and a choice between them.
    /// </remarks>
    private static Icon CreateIcon(Theme theme)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Read from the theme rather than fixed, and from the text token rather
            // than the softer one the widget's own bars use. Those bars are one element
            // among several across a wide strip, where this is a 16px mark that has to
            // be picked out of a row of other apps' icons at a glance.
            using var pen = new Pen(Ink(theme), 4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            float[] halfHeights = [6f, 10f, 4f, 8f];
            float x = 5.5f;

            foreach (float half in halfHeights)
            {
                g.DrawLine(pen, x, 16f - half, x, 16f + half);
                x += 7f;
            }
        }

        // Icon.FromHandle does not own the handle, so the HICON is released in Dispose.
        IntPtr handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>The theme's primary text color, as GDI wants it.</summary>
    private static Color Ink(Theme theme) =>
        theme.TextPrimary is System.Windows.Media.SolidColorBrush brush
            ? Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B)
            : Color.White;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _theme.Changed -= OnThemeChanged;
        _notifyIcon.Visible = false;

        IntPtr handle = _icon.Handle;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _menuFont?.Dispose();
        _defaultItemFont?.Dispose();
        _icon.Dispose();
        DestroyIcon(handle);
    }
}
