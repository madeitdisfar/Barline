using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TaskbarMusicWidget.Settings;
using TaskbarMusicWidget.Startup;

namespace TaskbarMusicWidget.Tray;

/// <summary>
/// Notification-area icon and menu — the widget's only chrome, since a taskbar
/// overlay has nowhere else to put settings or a way to quit.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly AutoStartService _autoStart;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _visualizerItem;

    /// <summary>Colour-mode items, keyed by the mode each one selects.</summary>
    private readonly Dictionary<VisualizerColorMode, ToolStripMenuItem> _colorItems = new();

    private readonly Icon _icon;

    private bool _disposed;

    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? VisualizerToggled;
    public event EventHandler? RestartVisualizerRequested;
    public event EventHandler<VisualizerColorMode>? VisualizerColorModeChanged;

    public TrayIcon(AutoStartService autoStart, WidgetSettings settings)
    {
        _autoStart = autoStart;

        _autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = _autoStart.IsEnabled,
        };
        _autoStartItem.CheckedChanged += (_, _) => _autoStart.SetEnabled(_autoStartItem.Checked);

        _visualizerItem = new ToolStripMenuItem("Show visualizer")
        {
            CheckOnClick = true,
            Checked = settings.VisualizerEnabled,
        };
        _visualizerItem.CheckedChanged += (_, _) =>
            VisualizerToggled?.Invoke(this, _visualizerItem.Checked);

        // Manual fallback: the watchdog recovers a stalled capture on its own, but
        // this lets the user force it immediately if the visualiser ever stops
        // responding to audio.
        var restartItem = new ToolStripMenuItem("Restart visualizer");
        restartItem.Click += (_, _) => RestartVisualizerRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(_visualizerItem);
        _menu.Items.Add(BuildColorMenu(settings.VisualizerColor));
        _menu.Items.Add(restartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _icon = CreateIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Taskbar Music Widget",
            Visible = true,
            ContextMenuStrip = _menu,
        };
    }

    /// <summary>Opens the menu at the cursor, for right-clicks on the widget itself.</summary>
    public void ShowContextMenu() => _menu.Show(Control.MousePosition);

    /// <summary>
    /// Builds the visualiser-colour submenu as a mutually exclusive group.
    /// </summary>
    /// <remarks>
    /// <see cref="VisualizerColorMode.Custom"/> is intentionally absent: picking a
    /// colour needs a picker, which belongs in the settings window. It stays
    /// selectable by editing settings.json, and a file already set to Custom is left
    /// alone unless the user chooses another mode here.
    /// </remarks>
    private ToolStripMenuItem BuildColorMenu(VisualizerColorMode current)
    {
        var parent = new ToolStripMenuItem("Visualizer color");

        (VisualizerColorMode Mode, string Label)[] entries =
        [
            (VisualizerColorMode.Default, "Default"),
            (VisualizerColorMode.SystemAccent, "Windows accent"),
            (VisualizerColorMode.AlbumArt, "From album art"),
        ];

        foreach (var (mode, label) in entries)
        {
            // No CheckOnClick: these are radio behaviour, so the check state follows
            // the selected mode rather than toggling per item.
            var item = new ToolStripMenuItem(label) { Checked = mode == current };
            item.Click += (_, _) => SelectColorMode(mode);

            _colorItems[mode] = item;
            parent.DropDownItems.Add(item);
        }

        return parent;
    }

    private void SelectColorMode(VisualizerColorMode mode)
    {
        foreach (var (candidate, item) in _colorItems)
            item.Checked = candidate == mode;

        VisualizerColorModeChanged?.Invoke(this, mode);
    }

    /// <summary>
    /// Draws the tray icon rather than shipping an .ico, so it always matches the
    /// widget's own visualiser motif and stays crisp at any tray size.
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
