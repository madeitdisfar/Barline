using System.Runtime.InteropServices;

namespace Barline.Shell;

/// <summary>
/// Win32 interop surface needed to track and shadow the Windows 11 taskbar.
/// Deliberately minimal: we only depend on <c>Shell_TrayWnd</c>'s geometry and
/// visibility, which is the most stable taskbar surface available to us.
/// </summary>
internal static class NativeMethods
{
    // ---- Structures -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;

        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    // ---- Constants --------------------------------------------------------

    internal const string TaskbarClass = "Shell_TrayWnd";
    /// <summary>Taskbar window class used on secondary monitors.</summary>
    internal const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    // SetWindowPos
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_NOOWNERZORDER = 0x0200;

    // ShowWindow
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;

    // Window styles
    internal const int GWL_EXSTYLE = -20;
    internal const int GWLP_HWNDPARENT = -8;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TRANSPARENT = 0x00000020;

    // WinEvent hooks
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    internal const uint EVENT_OBJECT_SHOW = 0x8002;
    internal const uint EVENT_OBJECT_HIDE = 0x8003;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // AppBar
    internal const uint ABM_GETSTATE = 0x00000004;
    internal const int ABS_AUTOHIDE = 0x0000001;

    // Monitors
    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // Window messages
    internal const int WM_DPICHANGED = 0x02E0;
    internal const int WM_DISPLAYCHANGE = 0x007E;
    internal const int WM_SETTINGCHANGE = 0x001A;

    // ---- Window discovery / geometry --------------------------------------

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    /// <summary>
    /// Finds the next top-level window of a class, which is how every secondary
    /// taskbar is walked without enumerating the whole desktop as <c>EnumWindows</c>
    /// would.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(
        IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ---- Window styles ----------------------------------------------------

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    // ---- DPI --------------------------------------------------------------

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    // ---- Pointer ----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Where the pointer is, in physical screen pixels.
    /// </summary>
    /// <remarks>
    /// Polled rather than handled as an event: the lyrics panel is
    /// <c>WS_EX_TRANSPARENT</c>, so mouse messages pass straight through it and it
    /// never receives an enter or leave of its own.
    /// </remarks>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    // ---- Monitors ---------------------------------------------------------

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---- Displays ---------------------------------------------------------

    /*
        Display identity comes from the DisplayConfig API rather than from the GDI
        device name. "\\.\DISPLAY2" is a slot rather than a monitor: unplug two screens
        and reconnect them the other way round and the two names swap, so a setting
        saved against one would quietly start meaning the other. The DisplayConfig
        target carries the monitor's own device path, which is built from its EDID and
        survives being unplugged, moved to another port, or renumbered.
    */

    /// <summary>QDC_ONLY_ACTIVE_PATHS: the displays currently part of the desktop.</summary>
    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    internal const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>The panel built into the machine, which usually carries no EDID name.</summary>
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    internal const int ERROR_SUCCESS = 0;

    internal const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;

        // An int rather than a marshaled bool. A bool would make the struct
        // non-blittable, which would copy every element of the path array on the way
        // in and out for the sake of a field nothing here reads.
        public int targetAvailable;

        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>
    /// A mode entry, declared as its size rather than as its shape.
    /// </summary>
    /// <remarks>
    /// <c>QueryDisplayConfig</c> insists on somewhere to write the modes even when the
    /// caller wants only the paths. It is a tagged union of three layouts and nothing
    /// here reads any of them, so transcribing all three would be sixty lines of
    /// interop whose only job would be to stay correct enough to ignore. The size is
    /// the part that has to be right, and a test pins it.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct DISPLAYCONFIG_MODE_INFO
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        /// <summary>What the monitor calls itself, which is often nothing at all.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        /// <summary>The stable identity. See the note at the top of this section.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    /// <summary>
    /// <c>MONITORINFOEX</c>, which is <c>MONITORINFO</c> plus the GDI device name.
    /// </summary>
    /// <remarks>
    /// That name is the only thing joining a monitor handle to a DisplayConfig path,
    /// which is why this variant exists alongside the plain struct above rather than
    /// replacing it.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    // ---- AppBar (auto-hide state) -----------------------------------------

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // ---- WinEvent hooks ---------------------------------------------------

    internal delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---- Misc -------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Returns the window class name, or an empty string on failure.</summary>
    internal static string GetWindowClass(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return string.Empty;
        var sb = new System.Text.StringBuilder(256);
        int len = GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }
}
