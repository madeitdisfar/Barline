using System.Runtime.InteropServices;
using System.Windows.Threading;
using Barline.Diagnostics;
using static Barline.Shell.NativeMethods;

namespace Barline.Shell;

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

    /// <summary>
    /// How long acquisition keeps retrying after the displays change.
    /// </summary>
    /// <remarks>
    /// Explorer creates a secondary monitor's taskbar some time after the display
    /// change that announced the monitor, so a single attempt on the event usually
    /// looks at a desktop that does not have it yet. The reconcile timer retries
    /// across this window instead, which costs a few enumerations after a plug-in and
    /// nothing at all the rest of the time.
    /// </remarks>
    private static readonly TimeSpan AcquireRetryWindow = TimeSpan.FromSeconds(5);

    private IntPtr _locationHook;
    private IntPtr _foregroundHook;
    private IntPtr _taskbarHwnd;
    private uint _explorerPid;
    private string? _targetDisplayId;
    private DateTime _retryUntil;
    private bool _started;
    private bool _disposed;

    public TaskbarState Current { get; private set; } = TaskbarState.Unavailable;

    public IntPtr TaskbarHandle => _taskbarHwnd;

    /// <summary>
    /// The display whose taskbar the widget should ride, or null for the primary's.
    /// </summary>
    /// <remarks>
    /// Setting it re-acquires immediately, so the widget moves as soon as the choice
    /// is made rather than at the next restart. An id naming a display that is not
    /// connected is not an error: acquisition falls back to the primary and picks the
    /// display up again if it returns.
    /// </remarks>
    public string? TargetDisplayId
    {
        get => _targetDisplayId;
        set
        {
            if (string.Equals(_targetDisplayId, value, StringComparison.OrdinalIgnoreCase))
                return;

            _targetDisplayId = value;

            if (!_started) return;

            AcquireTaskbar();
            Reconcile(force: true);
        }
    }

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
        _started = true;
        AcquireTaskbar();
        InstallHooks();
        _reconcileTimer.Start();
        Reconcile(force: true);
    }

    /// <summary>
    /// Called when the desktop's display layout changed.
    /// </summary>
    /// <remarks>
    /// Re-acquires rather than only re-placing. The taskbar handle survives a monitor
    /// being added, so nothing else here would notice that the display the user asked
    /// for has just come back.
    /// </remarks>
    public void HandleDisplayChange()
    {
        _retryUntil = DateTime.UtcNow + AcquireRetryWindow;
        AcquireTaskbar();
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

    /// <summary>
    /// Picks the taskbar to ride and notes which process owns it.
    /// </summary>
    /// <remarks>
    /// The whole desktop is swept rather than just <c>Shell_TrayWnd</c>, because the
    /// user may have asked for a secondary monitor's. Every taskbar belongs to the same
    /// Explorer, so the process id this reads is the same whichever one is chosen, and
    /// the hooks built around it do not have to be rebuilt when the choice changes.
    /// </remarks>
    private void AcquireTaskbar()
    {
        var taskbars = Displays.Taskbars();

        _taskbarHwnd = Displays.Choose(Displays.PrimaryTaskbar(), taskbars, _targetDisplayId);
        _explorerPid = 0;

        if (_taskbarHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(_taskbarHwnd, out _explorerPid);
        }

        if (_targetDisplayId is null) return;

        // Only worth a line when there was a choice to get wrong.
        bool onTarget = taskbars.Any(t =>
            t.Hwnd == _taskbarHwnd
            && string.Equals(t.DisplayId, _targetDisplayId, StringComparison.OrdinalIgnoreCase));

        DebugLog.Write(
            $"taskbar acquired: 0x{_taskbarHwnd:X} of {taskbars.Count}, "
            + (onTarget ? "on the chosen display" : "falling back to the primary"));

        // Stop retrying the moment the chosen display answers, so a plug-in costs a
        // tick or two rather than the whole window.
        if (onTarget) _retryUntil = DateTime.MinValue;
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
    // the hook, which is the UI thread. No marshaling required.
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
        // Re-acquire if Explorer died without us seeing the broadcast, or while a
        // display change is still settling. See AcquireRetryWindow.
        bool retrying = _targetDisplayId is not null && DateTime.UtcNow < _retryUntil;

        if (_taskbarHwnd == IntPtr.Zero || !IsWindow(_taskbarHwnd) || retrying)
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
