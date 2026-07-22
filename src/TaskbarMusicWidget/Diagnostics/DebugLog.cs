using System.IO;

namespace TaskbarMusicWidget.Diagnostics;

/// <summary>
/// Opt-in file logging for the window-tracking layer.
/// <para>
/// Enabled by setting <c>TMW_DEBUG=1</c>. This layer is difficult to debug
/// interactively: attaching a debugger changes foreground-window behaviour,
/// which is exactly what we need to observe. A log file avoids that.
/// </para>
/// </summary>
internal static class DebugLog
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("TMW_DEBUG") == "1";

    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taskbar-music-widget.log");

    private static readonly object Gate = new();

    static DebugLog()
    {
        if (!Enabled) return;
        try { File.WriteAllText(Path, $"=== session {DateTime.Now:HH:mm:ss} ==={Environment.NewLine}"); }
        catch { /* logging must never break the app */ }
    }

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
