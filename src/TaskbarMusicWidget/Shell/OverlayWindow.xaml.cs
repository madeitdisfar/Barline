using System.Windows;
using System.Windows.Interop;
using static TaskbarMusicWidget.Shell.NativeMethods;

namespace TaskbarMusicWidget.Shell;

/// <summary>
/// The widget's host window: a transparent, non-activating overlay pinned to the
/// left end of the taskbar.
/// <para>
/// It paints nothing of its own behind the content. The real taskbar's Mica /
/// acrylic material shows through, so the widget inherits the system backdrop
/// exactly and stays correct across theme, accent and transparency changes
/// without us reproducing any of it.
/// </para>
/// </summary>
internal partial class OverlayWindow : Window
{
    /// <summary>Widget width in logical (DPI-independent) pixels.</summary>
    private const double WidgetLogicalWidth = 280d;

    /// <summary>Gap between the taskbar's left edge and the widget, in logical pixels.</summary>
    private const double LeftInsetLogical = 0d;

    private readonly TaskbarTracker _tracker;
    private uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private bool _placedAtLeastOnce;

    public OverlayWindow(TaskbarTracker tracker)
    {
        _tracker = tracker;
        InitializeComponent();
        _tracker.Changed += OnTaskbarChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_NOACTIVATE  - clicking the widget never steals focus from the
        //                     user's active app.
        // WS_EX_TOOLWINDOW  - keeps the widget out of Alt+Tab.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

        // Explorer broadcasts this when it restarts; it is the signal to
        // re-acquire the taskbar handle.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        Apply(_tracker.Current);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            _tracker.HandleTaskbarRecreated();
        }
        else if (msg is WM_DPICHANGED or WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
        {
            // Let WPF finish its own DPI bookkeeping first, then re-place.
            Dispatcher.BeginInvoke(new Action(() => Apply(_tracker.Current)));
        }

        return IntPtr.Zero;
    }

    private void OnTaskbarChanged(object? sender, TaskbarState state) => Apply(state);

    private void Apply(TaskbarState state)
    {
        if (_hwnd == IntPtr.Zero) return;

        if (!state.IsAvailable || !state.ShouldShow)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            return;
        }

        double scale = state.Dpi / 96d;

        int width = (int)Math.Round(WidgetLogicalWidth * scale);
        int height = state.Rect.Height;
        int x = state.Rect.Left + (int)Math.Round(LeftInsetLogical * scale);
        int y = state.Rect.Top;

        // Position in physical pixels and re-assert topmost in the same call.
        // Re-asserting on every taskbar change is what keeps the widget from
        // being buried when other topmost windows come and go.
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        _placedAtLeastOnce = true;
        UpdateDiagnostics(state, width, height);
    }

    /// <summary>Phase 1 scaffolding — removed once the real design lands.</summary>
    private void UpdateDiagnostics(TaskbarState state, int width, int height)
    {
        DiagPrimary.Text = $"{width}×{height}px @ {state.Dpi} DPI";
        DiagSecondary.Text =
            $"taskbar {state.Rect}{(state.IsAutoHide ? " · autohide" : string.Empty)}";
    }

    /// <summary>True once the window has been positioned over a real taskbar.</summary>
    public bool HasBeenPlaced => _placedAtLeastOnce;
}
