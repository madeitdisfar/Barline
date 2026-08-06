using Microsoft.Win32;
using Barline.Diagnostics;

namespace Barline.Startup;

/// <summary>
/// Registers the widget to launch at sign-in.
/// </summary>
/// <remarks>
/// Uses the per-user <c>Run</c> key rather than a scheduled task or the
/// machine-wide key, so enabling it never requires elevation.
/// </remarks>
internal sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Barline";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value
                    && value.Contains(ValueName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"autostart read failed: {ex.Message}");
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                string? path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return;

                // Quoted: the install path may contain spaces.
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            DebugLog.Write($"autostart set to {enabled}");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"autostart write failed: {ex.Message}");
        }
    }
}
