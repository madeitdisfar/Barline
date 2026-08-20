using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Barline.Diagnostics;
using Barline.Settings;
using Barline.Ui;

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

    /// <summary>Physical pixels per logical pixel for a window, or 0 if it has none.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND: the radius Windows 11 gives its own menus.</summary>
    private const int DwmwcpRound = 2;

    private readonly Theme _theme;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _visualizerItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly FluentMenuRenderer _renderer;

    /// <summary>Redrawn whenever the theme moves, so it is never the wrong ink.</summary>
    private Icon _icon;

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
    /// Two scales, because two different things are being sized and they do not agree.
    /// </para>
    /// <para>
    /// Every pixel figure set from code is in device pixels, so a padding of 8 is 8
    /// physical pixels however the display is scaled, which on a 200% display is half
    /// the inset it looks like in the source. Those are multiplied by
    /// <see cref="Control.DeviceDpi"/> over 96, which is what turns them back into the
    /// logical pixels they are written as.
    /// </para>
    /// <para>
    /// The font is the other way around. Point sizes are already physical, and GDI
    /// converts them through the device's own DPI, so a 9pt menu font needs no help.
    /// It gets a scale only if WinForms has laid the menu out at a DPI the window
    /// disagrees with, which is measured rather than assumed: the ratio is 1 whenever
    /// the two agree, and this whole clause costs nothing.
    /// </para>
    /// </remarks>
    private void ApplyMetrics()
    {
        uint windowDpi = GetDpiForWindow(_menu.Handle);

        double pixels = _menu.DeviceDpi / 96d;
        double points = windowDpi == 0 ? 1d : windowDpi / (double)_menu.DeviceDpi;

        _renderer.Scale = pixels;

        DebugLog.Write(
            $"tray menu: windowDpi={windowDpi} deviceDpi={_menu.DeviceDpi} " +
            $"pixelScale={pixels:F2} fontScale={points:F2}");

        var font = CreateFont(points);
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

        // Bare surface above the first item and below the last, which is what keeps
        // their selection pills off the rounded corners.
        _menu.Padding = new Padding(0, Round(4, pixels), 0, Round(4, pixels));

        foreach (ToolStripItem item in _menu.Items)
        {
            item.Padding = item is ToolStripSeparator
                ? new Padding(0, Round(3, pixels), 0, Round(3, pixels))
                : new Padding(0, Round(9, pixels), 0, Round(9, pixels));

            // Margin rather than Padding for the horizontal inset. The dropdown's
            // layout resets an item's left and right padding, and the menu's own
            // padding with it, but it stacks items by their margins.
            item.Margin = new Padding(Round(4, pixels), 0, Round(4, pixels), 0);
        }
    }

    private static int Round(double value, double scale) => (int)Math.Round(value * scale);

    /// <summary>
    /// The menu font, at the size the display actually needs.
    /// </summary>
    /// <remarks>
    /// Segoe UI Variable Text is what Windows 11 sets menus in, and it is the optical
    /// size cut for body text rather than for headings. It ships with Windows 11 and the
    /// package requires that, but a font is a file and files go missing, so a machine
    /// without it falls back to Segoe UI rather than to whatever GDI substitutes, which
    /// is Microsoft Sans Serif and looks like a fault.
    /// </remarks>
    private static Font CreateFont(double scale)
    {
        float size = (float)(9d * scale);

        try
        {
            var font = new Font("Segoe UI Variable Text", size);
            if (font.Name == "Segoe UI Variable Text") return font;
            font.Dispose();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"tray menu font unavailable: {ex.Message}");
        }

        return new Font("Segoe UI", size);
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
