using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Barline.Settings;
using Barline.Startup;

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

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _visualizerItem;
    private readonly Icon _icon;

    private bool _disposed;

    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? VisualizerToggled;
    public event EventHandler? RestartVisualizerRequested;
    public event EventHandler? RestartRequested;
    public event EventHandler? SettingsRequested;

    public TrayIcon(WidgetSettings settings)
    {
        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        settingsItem.Font = new Font(settingsItem.Font, System.Drawing.FontStyle.Bold);

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

        _menu = new ContextMenuStrip();
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_visualizerItem);
        _menu.Items.Add(restartVisualizerItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(restartItem);
        _menu.Items.Add(exitItem);

        _icon = CreateIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Barline",
            Visible = true,
            ContextMenuStrip = _menu,
        };
    }

    /// <summary>Opens the menu at the cursor, for right-clicks on the widget itself.</summary>
    public void ShowContextMenu() => _menu.Show(Control.MousePosition);

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
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Round caps give the same pill-shaped bars the widget draws.
            using var pen = new Pen(Color.White, 4f)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;

        IntPtr handle = _icon.Handle;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
        DestroyIcon(handle);
    }
}
