using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Barline.Diagnostics;

namespace Barline.Platform;

/// <summary>
/// Hands back the pages the app touched once and will never touch again.
/// </summary>
/// <remarks>
/// <para>
/// A widget is judged by the number beside its name in Task Manager, and Barline's
/// was around 150 MB for something whose live working set is nearer 10. Measured, on
/// a session that had been up for eight minutes: trimming took the working set from
/// 311 MB to 4 MB, and twenty seconds of ordinary running brought it back only to
/// 10.4 MB. The managed heap over the same run was 4.6 MB with no collection at all,
/// so none of it was allocation and none of it was a leak.
/// </para>
/// <para>
/// What it was is startup. Starting a self-contained WPF app touches an enormous
/// number of pages once each: the bundle, the JIT, WPF's own initialization, and the
/// graphics driver that comes with creating a D3D device. None of them is read again,
/// and on a machine with memory to spare nothing ever forces them out, so they sit in
/// the working set for the rest of the session looking exactly like memory the app is
/// using.
/// </para>
/// <para>
/// So they are given back deliberately, at the few moments where a burst of one-time
/// work has just finished: after startup settles, after a window nobody will open
/// again closes, and after the daily update check drags in the whole Store stack.
/// Windows would do this itself the moment anything else wanted the memory. Doing it
/// first is the difference between a widget that looks expensive and one that does
/// not.
/// </para>
/// <para>
/// This moves pages out of the working set rather than freeing them: private ones
/// that are still dirty are written to the page file once. That is the right trade
/// for pages that are provably dead, which the 10.4 MB is the measurement of, and it
/// is the reason this is never put on a repeating timer. Trimming during use only
/// faults the same pages straight back in, which costs a stutter and saves nothing.
/// </para>
/// </remarks>
internal static class WorkingSet
{
    /// <summary>
    /// How long to let a burst of one-time work finish before trimming.
    /// </summary>
    /// <remarks>
    /// Long enough for the slow parts of starting to be over: the first taskbar
    /// placement, the first media session, the loopback capture, and the license
    /// question the Store answers over the network. Trimming into the middle of any of
    /// those would take back pages that are about to be wanted again.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait after a window closes.
    /// </summary>
    /// <remarks>
    /// WPF tears a window down over a few dispatcher passes, so trimming the instant
    /// Closed is raised would run against pages the teardown is still reading.
    /// </remarks>
    private static readonly TimeSpan AfterWindow = TimeSpan.FromSeconds(3);

    private static DispatcherTimer? _pending;

    /// <summary>
    /// Trims once a burst of one-time work has had time to finish.
    /// </summary>
    public static void TrimWhenQuiet() => TrimIn(Settle);

    /// <summary>
    /// Trims after a window closes, for windows that are built once and thrown away.
    /// </summary>
    /// <remarks>
    /// The settings window is the expensive one. It is a large XAML tree that is
    /// created on first use, faults in a great deal of WPF that the widget alone never
    /// touches, and is then closed and dropped. Nothing about it is worth keeping
    /// resident for the days between openings.
    /// </remarks>
    public static void TrimWhenClosed(Window window) =>
        window.Closed += (_, _) => TrimIn(AfterWindow);

    /// <summary>
    /// Schedules a trim, folding the request into one already waiting.
    /// </summary>
    /// <remarks>
    /// Closing a settings window that has a document window open over it produces two
    /// requests a moment apart, and there is nothing for the second to reclaim. The
    /// one already scheduled fires later than either, so it covers both.
    /// </remarks>
    private static void TrimIn(TimeSpan delay)
    {
        if (_pending is not null) return;

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };

        // The timer itself rather than the field, so stopping cannot depend on what
        // the field happens to hold by the time this runs.
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _pending = null;
            Trim();
        };

        _pending = timer;
        timer.Start();
    }

    private static void Trim()
    {
        // The pseudo-handle, which needs no rights and never needs closing.
        if (!EmptyWorkingSet(GetCurrentProcess()))
        {
            DebugLog.Write($"working set: trim failed ({Marshal.GetLastWin32Error()})");
            return;
        }

        DebugLog.Write("working set: trimmed");
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
