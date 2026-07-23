using System.Runtime.InteropServices;

namespace TaskbarMusicWidget.Shell;

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

    // ---- Monitors ---------------------------------------------------------

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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
