using System.Windows.Automation;
using Barline.Diagnostics;
using static Barline.Shell.NativeMethods;

namespace Barline.Shell;

/// <summary>
/// Where the clock starts on a taskbar that has no notification area to measure.
/// </summary>
/// <remarks>
/// <para>
/// A secondary taskbar carries no tray icons, only a clock, and Explorer keeps none of
/// the old child windows on it: enumerating one turns up the XAML bridge, an invisible
/// <c>Start</c>, and the task list, and nothing else. The clock exists only in the
/// automation tree, so the far end of a second display's taskbar can be found only by
/// asking UI Automation for it.
/// </para>
/// <para>
/// That answer costs 20 to 25 milliseconds warm, and 80 or more on the first call of
/// the process. Measured, and far too slow to sit in a probe that runs every second on
/// the thread that draws the widget. So it is asked on a background thread and the
/// answer is kept: the probe reads whatever the last one found and never waits, and the
/// answer announces itself when it lands rather than waiting for the next probe.
/// </para>
/// <para>
/// It is asked again on a slow cadence because the only thing that moves the clock
/// without moving the taskbar is the width of what it says, which changes when the
/// hour goes from one digit to two and when the date rolls over. A move of a few pixels
/// twice a day does not deserve a faster question than this.
/// </para>
/// </remarks>
internal sealed class ClockEdge
{
    /// <summary>
    /// The clock's class in the taskbar's automation tree.
    /// </summary>
    /// <remarks>
    /// A type name from the shell's own XAML rather than anything localized, so it
    /// holds on a Windows in any language. It is a private detail of a Windows build
    /// all the same, which is why not finding it is an ordinary outcome here and leaves
    /// the widget at the end of the taskbar it has always been at.
    /// </remarks>
    private const string ClockClass = "NamedContainerAutomationPeer";

    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);

    private const int Unknown = int.MinValue;

    private IntPtr _taskbar;
    private RECT _bounds;
    private DateTime _asked;
    private int _asking;

    /// <summary>
    /// Written by the background query and read by the probe, which is why it is
    /// <c>volatile</c> and an <c>int</c> rather than an <c>int?</c>: a reference-sized
    /// write is atomic where a nullable struct's is not.
    /// </summary>
    private volatile int _left = Unknown;

    /// <summary>
    /// Raised on a background thread when the answer changes.
    /// </summary>
    /// <remarks>
    /// Without it the answer would wait for the next probe, and the widget's first
    /// appearance can fall inside that second: measured, it showed at the near end of
    /// the taskbar and slid across a third of a second later. Announcing the answer
    /// lets the placement be right the first time it is seen.
    /// </remarks>
    public event EventHandler? Answered;

    /// <summary>
    /// The clock's left edge in physical pixels, or null if it is not known yet.
    /// </summary>
    /// <param name="taskbar">The taskbar to measure.</param>
    /// <param name="bounds">That taskbar's rectangle.</param>
    public int? For(IntPtr taskbar, RECT bounds)
    {
        // A different taskbar, or the same one resized, is a different question. The
        // old answer is dropped rather than shown while the new one is fetched, since
        // an edge from a display that is no longer there would place the widget off it.
        if (taskbar != _taskbar || !bounds.Equals(_bounds))
        {
            _taskbar = taskbar;
            _bounds = bounds;
            _left = Unknown;
            _asked = default;
        }

        if (DateTime.UtcNow - _asked >= Refresh && Interlocked.Exchange(ref _asking, 1) == 0)
        {
            _asked = DateTime.UtcNow;
            Task.Run(() => Ask(taskbar, bounds));
        }

        int left = _left;
        return left == Unknown ? null : left;
    }

    private void Ask(IntPtr taskbar, RECT bounds)
    {
        try
        {
            int? found = Find(taskbar, bounds);

            // Only if it is still an answer to the question that was asked. The
            // taskbar can be replaced while a query is in flight.
            if (taskbar != _taskbar || !bounds.Equals(_bounds)) return;

            int answer = found ?? Unknown;
            if (answer == _left) return;

            _left = answer;
            Answered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"clock edge: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _asking, 0);
        }
    }

    /// <summary>
    /// Asks the automation tree, on a background thread.
    /// </summary>
    /// <remarks>
    /// The leftmost match on the far half of the taskbar. One match is what a Windows 11
    /// secondary taskbar has, but what is wanted is where the free stretch ends, so more
    /// than one is answered by the first of them rather than by giving up.
    /// </remarks>
    private static int? Find(IntPtr taskbar, RECT bounds)
    {
        var root = AutomationElement.FromHandle(taskbar);

        var matches = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, ClockClass));

        int middle = bounds.Left + (bounds.Width / 2);
        int? edge = null;

        foreach (AutomationElement match in matches)
        {
            var rect = match.Current.BoundingRectangle;
            if (rect.IsEmpty) continue;

            int left = (int)Math.Round(rect.Left);
            if (left <= middle || left >= bounds.Right) continue;

            edge = edge is int already ? Math.Min(already, left) : left;
        }

        return edge;
    }
}
