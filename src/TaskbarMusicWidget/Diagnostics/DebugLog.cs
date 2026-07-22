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

    /// <summary>Roll the file past this size rather than letting it grow forever.</summary>
    private const long MaxBytes = 1_000_000;

    static DebugLog()
    {
        if (!Enabled) return;
        try
        {
            // Append rather than truncate. Instances share this file, and a second
            // launch rejected by the single-instance guard would otherwise wipe the
            // running instance's history before exiting — which is exactly when the
            // log is most wanted.
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                File.Delete(Path);

            File.AppendAllText(
                Path,
                $"{Environment.NewLine}=== session {DateTime.Now:HH:mm:ss} (pid {Environment.ProcessId}) ==={Environment.NewLine}");
        }
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
