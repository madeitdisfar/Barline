using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Barline.Diagnostics;
using Barline.Settings;
using Barline.Ui;

// The shell layer's interop, aliased rather than imported: this file declares a
// P/Invoke of its own for the notification area, and a plain using static would put
// two of some names in scope at once.
using Win32 = Barline.Shell.NativeMethods;

namespace Barline.Tray;

/// <summary>
/// Notification-area icon, the widget's only chrome besides the settings window.
/// </summary>
/// <remarks>
/// <para>
/// WinForms is here for <see cref="NotifyIcon"/> alone, which is the one piece of this
/// WPF has no answer for. The menu it opens is WPF: see <see cref="TrayMenu"/> for why.
/// </para>
/// <para>
/// The menu holds quick actions only. Anything that is configuration rather than a
/// one-off action lives in the settings window instead, which also avoids two places
/// showing the same state and drifting out of sync.
/// </para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Theme _theme;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenu _menu;

    /// <summary>Redrawn whenever the theme moves, so it is never the wrong ink.</summary>
    private Icon _icon;

    /// <summary>Whether the icon is carrying the update dot.</summary>
    private bool _update;

    private bool _disposed;

    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? VisualizerToggled;
    public event EventHandler? RestartVisualizerRequested;
    public event EventHandler? RestartRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? UpdateRequested;

    public TrayIcon(WidgetSettings settings, Theme theme)
    {
        _theme = theme;
        _menu = new TrayMenu(settings, theme);

        _menu.ExitRequested += (_, e) => ExitRequested?.Invoke(this, e);
        _menu.VisualizerToggled += (_, enabled) => VisualizerToggled?.Invoke(this, enabled);
        _menu.RestartVisualizerRequested += (_, e) => RestartVisualizerRequested?.Invoke(this, e);
        _menu.RestartRequested += (_, e) => RestartRequested?.Invoke(this, e);
        _menu.SettingsRequested += (_, e) => SettingsRequested?.Invoke(this, e);
        _menu.UpdateRequested += (_, e) => UpdateRequested?.Invoke(this, e);

        _icon = CreateIcon(_theme, _update);
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Barline",
            Visible = true,
        };

        // Opened by hand rather than by assigning ContextMenuStrip, which only accepts
        // the WinForms menu this no longer has. MouseUp rather than MouseClick: the
        // notification area is one of the places where the click event is not reliably
        // raised for the right button.
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowContextMenu();
        };

        // Last, because the handler redraws the icon and so needs the notification
        // area to already have one.
        _theme.Changed += OnThemeChanged;
    }

    /// <summary>Opens the menu at the cursor, for the tray and for the widget alike.</summary>
    public void ShowContextMenu()
    {
        if (!Win32.GetCursorPos(out var point)) return;

        _menu.Show(new Point(point.X, point.Y));
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RefreshIcon();

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

        _icon = CreateIcon(_theme, _update);
        _notifyIcon.Icon = _icon;

        IntPtr handle = previous.Handle;
        previous.Dispose();
        DestroyIcon(handle);
    }

    /// <summary>
    /// Reflects a visualizer-visibility change made elsewhere (the settings window)
    /// so the menu's checkmark does not go stale.
    /// </summary>
    public void SetVisualizerChecked(bool enabled) => _menu.SetVisualizerChecked(enabled);

    /// <summary>Whether the menu is on screen.</summary>
    public bool MenuIsOpen => _menu.IsOpen;

    /// <summary>
    /// Says that an update is waiting, which the icon and the menu both show.
    /// </summary>
    /// <remarks>
    /// The icon is the point of it. A user who never opens the settings window would
    /// otherwise never learn there is anything to install, and the notification area is
    /// the one piece of this app that is on screen whether or not anything is playing.
    /// A dot is also as loud as this deserves to be: nothing is wrong, and the app the
    /// user already has keeps working.
    /// </remarks>
    public void SetUpdateAvailable(bool available, string? version)
    {
        _menu.SetUpdateAvailable(available, version);

        if (available == _update) return;

        _update = available;
        RefreshIcon();
    }

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
    /// <param name="update">Whether to mark the icon with the update dot.</param>
    private static Icon CreateIcon(Theme theme, bool update)
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

            if (update) DrawUpdateDot(g, theme);
        }

        // Icon.FromHandle does not own the handle, so the HICON is released in Dispose.
        IntPtr handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>
    /// Marks the icon with an accent dot in the corner.
    /// </summary>
    /// <remarks>
    /// The gap around it is punched out of what is already drawn rather than filled
    /// with a background color, because the notification area's background is the
    /// taskbar's material and no color would match it. Copying transparency over the
    /// bars leaves a real hole, which reads correctly on any taskbar and at the 16px
    /// the tray usually draws this at.
    /// </remarks>
    private static void DrawUpdateDot(Graphics g, Theme theme)
    {
        var previous = g.CompositingMode;

        g.CompositingMode = CompositingMode.SourceCopy;
        using (var hole = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(hole, 15f, 15f, 17f, 17f);
        }

        g.CompositingMode = previous;

        var accent = theme.Accent;
        using var brush = new SolidBrush(
            Color.FromArgb(accent.A, accent.R, accent.G, accent.B));

        g.FillEllipse(brush, 18f, 18f, 12f, 12f);
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
        _menu.Dispose();
        _notifyIcon.Visible = false;

        IntPtr handle = _icon.Handle;
        _notifyIcon.Dispose();
        _icon.Dispose();
        DestroyIcon(handle);
    }
}
