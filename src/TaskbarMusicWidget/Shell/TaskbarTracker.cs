using System.Runtime.InteropServices;
using System.Windows.Threading;
using TaskbarMusicWidget.Diagnostics;
using static TaskbarMusicWidget.Shell.NativeMethods;

namespace TaskbarMusicWidget.Shell;

/// <summary>
/// A snapshot of everything the widget needs to know about the taskbar.
/// Geometry is in <b>physical pixels</b>.
/// </summary>
internal readonly record struct TaskbarState(
    bool IsAvailable,
    bool ShouldShow,
    RECT Rect,
    uint Dpi,
    bool IsAutoHide)
{
    public static readonly TaskbarState Unavailable =
        new(false, false, default, 96, false);
}

/// <summary>
/// Tracks <c>Shell_TrayWnd</c> and publishes state changes.
/// <para>
/// The widget is a <i>satellite</i> of the taskbar rather than an independent
/// topmost window. Mirroring the taskbar's rect, DPI and visibility is what makes
/// auto-hide, fullscreen apps, DPI changes and Explorer restarts all behave
/// correctly through a single mechanism.
/// </para>
/// <para>
/// Auto-hide needs no special positioning logic: when the taskbar retracts it
/// simply slides off-screen, and because we mirror its rect we slide with it.
/// </para>
/// </summary>
internal sealed class TaskbarTracker : IDisposable
{
    private readonly DispatcherTimer _reconcileTimer;

    // The delegate must be held in a field: SetWinEventHook stores a raw pointer
    // and the GC would otherwise collect a locally-scoped delegate.
    private readonly WinEventProc _winEventProc;

    private IntPtr _locationHook;
    private IntPtr _foregroundHook;
    private IntPtr _taskbarHwnd;
    private uint _explorerPid;
    private bool _disposed;

    public TaskbarState Current { get; private set; } = TaskbarState.Unavailable;

    public IntPtr TaskbarHandle => _taskbarHwnd;

    public event EventHandler<TaskbarState>? Changed;

    public TaskbarTracker()
    {
        _winEventProc = OnWinEvent;

        // Safety net only. The hooks below do the real work; this catches the
        // rare transitions that raise no event we listen for.
        _reconcileTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _reconcileTimer.Tick += (_, _) => Reconcile();
    }

    public void Start()
    {
        AcquireTaskbar();
        InstallHooks();
        _reconcileTimer.Start();
        Reconcile(force: true);
    }

    /// <summary>
    /// Called when the shell broadcasts <c>TaskbarCreated</c> (Explorer restarted).
    /// The old handle is dead, so hooks must be rebuilt around the new one.
    /// </summary>
    public void HandleTaskbarRecreated()
    {
        RemoveHooks();
        AcquireTaskbar();
        InstallHooks();
        Reconcile(force: true);
    }

    private void AcquireTaskbar()
    {
        _taskbarHwnd = FindWindow(TaskbarClass, null);
        _explorerPid = 0;
        if (_taskbarHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(_taskbarHwnd, out _explorerPid);
        }
    }

    private void InstallHooks()
    {
        // Scope location changes to Explorer only. This event fires for *every*
        // window move system-wide; without the process filter it is a firehose.
        if (_explorerPid != 0)
        {
            _locationHook = SetWinEventHook(
                EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _winEventProc, _explorerPid, 0, WINEVENT_OUTOFCONTEXT);
        }

        // Foreground changes must be watched globally so we can detect a
        // fullscreen app taking over and get out of its way.
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        DebugLog.Write(
            $"hooks installed: location=0x{_locationHook:X} foreground=0x{_foregroundHook:X} " +
            $"explorerPid={_explorerPid} taskbar=0x{_taskbarHwnd:X}");
    }

    private void RemoveHooks()
    {
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    // WINEVENT_OUTOFCONTEXT delivers the callback on the thread that installed
    // the hook, which is the UI thread. No marshalling required.
    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_disposed) return;

        // Ignore location noise from Explorer windows that aren't the taskbar.
        if (eventType == EVENT_OBJECT_LOCATIONCHANGE && hwnd != _taskbarHwnd)
            return;

        Reconcile();
    }

    private void Reconcile() => Reconcile(force: false);

    private void Reconcile(bool force)
    {
        if (_disposed) return;

        var next = Probe();
        if (force || !next.Equals(Current))
        {
            DebugLog.Write(
                $"state change: available={next.IsAvailable} shouldShow={next.ShouldShow} " +
                $"rect={next.Rect} dpi={next.Dpi} autoHide={next.IsAutoHide}");
            Current = next;
            Changed?.Invoke(this, next);
        }
    }

    private TaskbarState Probe()
    {
        // Re-acquire if Explorer died without us seeing the broadcast.
        if (_taskbarHwnd == IntPtr.Zero || !IsWindow(_taskbarHwnd))
        {
            AcquireTaskbar();
            if (_taskbarHwnd == IntPtr.Zero)
                return TaskbarState.Unavailable;
        }

        if (!IsWindowVisible(_taskbarHwnd) || !GetWindowRect(_taskbarHwnd, out var rect))
            return TaskbarState.Unavailable;

        uint dpi = GetDpiForWindow(_taskbarHwnd);
        if (dpi == 0) dpi = 96;

        bool autoHide = IsAutoHideEnabled();
        bool fullscreen = IsFullscreenAppForeground();

        return new TaskbarState(
            IsAvailable: true,
            ShouldShow: !fullscreen,
            Rect: rect,
            Dpi: dpi,
            IsAutoHide: autoHide);
    }

    private static bool IsAutoHideEnabled()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        var state = SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64();
        return (state & ABS_AUTOHIDE) != 0;
    }

    /// <summary>
    /// True when the foreground window covers its entire monitor. Windows drops
    /// the taskbar behind such windows, and the widget must follow suit or it
    /// will float over games and full-screen video.
    /// </summary>
    private bool IsFullscreenAppForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _taskbarHwnd) return false;

        // The desktop is always "fullscreen"; it is not an app taking over.
        var cls = GetWindowClass(fg);
        if (cls is "Progman" or "WorkerW" or TaskbarClass or SecondaryTaskbarClass)
            return false;

        if (!GetWindowRect(fg, out var wr)) return false;

        var monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        var m = mi.rcMonitor;
        bool covers = wr.Left <= m.Left && wr.Top <= m.Top
            && wr.Right >= m.Right && wr.Bottom >= m.Bottom;

        DebugLog.Write($"fullscreen probe: fg=0x{fg:X} cls={cls} rect={wr} mon={m} covers={covers}");
        return covers;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconcileTimer.Stop();
        RemoveHooks();
    }
}
