using Barline.Diagnostics;
using static Barline.Shell.NativeMethods;

namespace Barline.Shell;

/// <summary>
/// A display the widget could sit on, which means one that has a taskbar.
/// </summary>
/// <param name="Id">
/// The monitor's device path, stable across unplugging and renumbering. This is what
/// a setting stores.
/// </param>
/// <param name="Name">What to call it on screen. Never empty; see <c>NameFor</c>.</param>
/// <param name="IsPrimary">Whether Windows treats this as the main display.</param>
internal readonly record struct DisplayTarget(string Id, string Name, bool IsPrimary);

/// <summary>A taskbar window and the display it is on.</summary>
internal readonly record struct TaskbarWindow(IntPtr Hwnd, string? DisplayId);

/// <summary>
/// What the settings picker needs to know.
/// </summary>
/// <param name="WithTaskbars">The displays the widget could actually be put on.</param>
/// <param name="Attached">
/// How many displays are connected at all. The difference between the two is the
/// whole reason the picker can offer one choice on a two-monitor desk, so the card
/// says so rather than leaving it to be worked out.
/// </param>
internal readonly record struct DisplaySurvey(
    IReadOnlyList<DisplayTarget> WithTaskbars, int Attached);

/// <summary>
/// Finds the taskbars on the desktop and tells their displays apart.
/// </summary>
/// <remarks>
/// <para>
/// The widget is a satellite of one taskbar, so "which monitor" is really "which
/// taskbar". That distinction matters: Windows draws a taskbar on every display only
/// if asked to, and on a machine that has not been asked there is exactly one, on the
/// primary. A display with no taskbar is not somewhere the widget can go, which is why
/// nothing here enumerates monitors in general.
/// </para>
/// </remarks>
internal static class Displays
{
    /// <summary>
    /// Every taskbar currently on the desktop, primary first, each tagged with the
    /// display it sits on.
    /// </summary>
    /// <remarks>
    /// The display config is read once for the whole sweep rather than per window,
    /// since it describes the desktop rather than any one taskbar.
    /// </remarks>
    public static IReadOnlyList<TaskbarWindow> Taskbars()
    {
        var byDevice = ReadDisplayConfig();
        var found = new List<TaskbarWindow>();

        foreach (var hwnd in Handles())
            found.Add(new TaskbarWindow(hwnd, Describe(hwnd, byDevice)?.Id));

        return found;
    }

    /// <summary>The displays that have a taskbar, and how many exist, for the picker.</summary>
    public static DisplaySurvey Survey()
    {
        var byDevice = ReadDisplayConfig();
        var found = new List<DisplayTarget>();

        foreach (var hwnd in Handles())
        {
            if (Describe(hwnd, byDevice) is not { } target) continue;

            // Two taskbars on one display is not a thing Windows does, but the picker
            // must not offer the same monitor twice if it ever becomes one.
            if (found.Any(d => d.Id == target.Id)) continue;

            found.Add(target);
        }

        // Every attached display has a config path whether or not Explorer drew a
        // taskbar on it, so the map is already the count.
        return new DisplaySurvey(found, byDevice.Count);
    }

    /// <summary>
    /// Picks the taskbar to ride.
    /// </summary>
    /// <param name="primary">The primary display's taskbar, or zero if there is none.</param>
    /// <param name="all">Every taskbar found, in any order.</param>
    /// <param name="preferred">The display the user asked for, or null for no preference.</param>
    /// <remarks>
    /// Split out from the acquiring so the rule can be tested without a desktop. The
    /// rule is the part with a guarantee attached: whatever happens, this returns a
    /// window if there is one to return. A widget that disappears because a monitor was
    /// unplugged reads as a crash, and the taskbar it was riding went with it, so the
    /// only decent answer is to fall back rather than to hold out for the display that
    /// was asked for.
    /// </remarks>
    public static IntPtr Choose(
        IntPtr primary, IReadOnlyList<TaskbarWindow> all, string? preferred)
    {
        if (!string.IsNullOrEmpty(preferred))
        {
            foreach (var taskbar in all)
            {
                if (taskbar.Hwnd == IntPtr.Zero || taskbar.DisplayId is null) continue;

                if (string.Equals(taskbar.DisplayId, preferred, StringComparison.OrdinalIgnoreCase))
                    return taskbar.Hwnd;
            }
        }

        if (primary != IntPtr.Zero) return primary;

        // No primary either, which happens for a moment while Explorer restarts.
        // Anything is better than nothing here, and by the next reconcile there will
        // be a real answer.
        foreach (var taskbar in all)
            if (taskbar.Hwnd != IntPtr.Zero) return taskbar.Hwnd;

        return IntPtr.Zero;
    }

    /// <summary>The primary display's taskbar, or zero.</summary>
    public static IntPtr PrimaryTaskbar() => FindWindow(TaskbarClass, null);

    /// <summary>
    /// Taskbar windows, primary first.
    /// </summary>
    /// <remarks>
    /// Order matters only in that the primary comes first, which is what makes the
    /// picker list the main display at the top.
    /// </remarks>
    private static IEnumerable<IntPtr> Handles()
    {
        var primary = PrimaryTaskbar();
        if (primary != IntPtr.Zero) yield return primary;

        var next = IntPtr.Zero;
        while ((next = FindWindowEx(IntPtr.Zero, next, SecondaryTaskbarClass, null)) != IntPtr.Zero)
            yield return next;
    }

    /// <summary>
    /// Identifies the display a window is on, given an already-read display config.
    /// </summary>
    private static DisplayTarget? Describe(
        IntPtr hwnd, IReadOnlyDictionary<string, DisplayTarget> byDevice)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return null;

        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoEx(monitor, ref info)) return null;

        if (!byDevice.TryGetValue(info.szDevice, out var target)) return null;

        // The primary flag comes from the monitor rather than from the display config,
        // which describes what is connected rather than which one Windows treats as
        // the main one.
        return target with { IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0 };
    }

    /// <summary>
    /// Maps each active GDI device name to the monitor behind it.
    /// </summary>
    /// <remarks>
    /// Returns an empty map on any failure rather than throwing. Every caller can carry
    /// on without it: an unidentified display simply cannot be picked, which leaves the
    /// widget where it already was.
    /// </remarks>
    private static IReadOnlyDictionary<string, DisplayTarget> ReadDisplayConfig()
    {
        var map = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);

        try
        {
            int rc = GetDisplayConfigBufferSizes(
                QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);

            if (rc != ERROR_SUCCESS || pathCount == 0) return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            rc = QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

            if (rc != ERROR_SUCCESS) return map;

            for (int i = 0; i < pathCount; i++)
            {
                if (SourceName(paths[i]) is not { Length: > 0 } device) continue;
                if (map.ContainsKey(device)) continue;

                var (id, name, internalPanel) = TargetName(paths[i]);
                if (id.Length == 0) continue;

                map[device] = new DisplayTarget(
                    id, NameFor(name, internalPanel, map.Count + 1), IsPrimary: false);
            }
        }
        catch (Exception ex)
        {
            // Nothing here is load-bearing enough to take the widget down for.
            DebugLog.Write($"display config unavailable: {ex.Message}");
        }

        return map;
    }

    private static string? SourceName(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS
            ? request.viewGdiDeviceName
            : null;
    }

    private static (string Id, string Name, bool Internal) TargetName(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS)
            return (string.Empty, string.Empty, false);

        return (
            request.monitorDevicePath ?? string.Empty,
            request.monitorFriendlyDeviceName ?? string.Empty,
            request.outputTechnology == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL);
    }

    /// <summary>
    /// What to call a display in the picker.
    /// </summary>
    /// <remarks>
    /// A monitor names itself through its EDID, and a laptop's built-in panel usually
    /// does not bother, so the friendly name is empty on exactly the machine most
    /// likely to be running this. Falling back to the GDI device name would be worse
    /// than useless, since it is a slot number that does not match the number Windows
    /// shows in Display settings. Naming it for what it is instead is both true and
    /// the thing a person would say.
    /// </remarks>
    private static string NameFor(string friendly, bool internalPanel, int ordinal)
    {
        if (friendly.Length > 0) return friendly;

        return internalPanel ? "Built-in display" : $"Display {ordinal}";
    }
}
