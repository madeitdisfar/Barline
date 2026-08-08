using Microsoft.Win32;
using Barline.Diagnostics;
using Barline.Platform;
using Windows.ApplicationModel;

namespace Barline.Startup;

/// <summary>
/// How Windows currently treats launching the widget at sign-in.
/// </summary>
/// <remarks>
/// Richer than a bool because a packaged app cannot simply assert this. Windows lets
/// the user or an administrator veto a startup task, and when they have, the app is
/// told so and cannot override it — a state a checkbox alone cannot represent
/// honestly.
/// </remarks>
internal enum AutoStartState
{
    Disabled,
    Enabled,

    /// <summary>
    /// Switched off outside the app. Only the user can restore it, from Task
    /// Manager's Startup apps tab — <c>RequestEnableAsync</c> will not.
    /// </summary>
    BlockedByUser,

    /// <summary>Switched off by administrative policy.</summary>
    BlockedByPolicy,

    /// <summary>Windows would not report it; treated as off.</summary>
    Unavailable,
}

/// <summary>
/// Registers the widget to launch at sign-in.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms, because the same binary can run either way. Unpackaged, this is
/// the per-user <c>Run</c> key — no elevation, and the app owns the setting outright.
/// </para>
/// <para>
/// Packaged, that key is not available: Windows ignores <c>Run</c> entries written by
/// a packaged app, so the same code would appear to succeed and silently do nothing.
/// The supported route is a startup task declared in the manifest, which the user
/// approves and can revoke. The manifest needs an entry whose TaskId matches
/// <see cref="StartupTaskId"/>:
/// </para>
/// <code>
/// &lt;Extensions&gt;
///   &lt;uap5:Extension Category="windows.startupTask"
///                   Executable="Barline.exe"
///                   EntryPoint="Windows.FullTrustApplication"&gt;
///     &lt;uap5:StartupTask TaskId="BarlineStartupTask"
///                       Enabled="false"
///                       DisplayName="Barline" /&gt;
///   &lt;/uap5:Extension&gt;
/// &lt;/Extensions&gt;
/// </code>
/// <para>
/// <c>Enabled="false"</c> deliberately: starting with Windows should be something the
/// user turns on, not something installing the app decides for them.
/// </para>
/// </remarks>
internal sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Barline";

    /// <summary>Must match the TaskId declared in the package manifest.</summary>
    internal const string StartupTaskId = "BarlineStartupTask";

    /// <summary>Whether this process is running from an MSIX package.</summary>
    public bool IsPackaged => PackageContext.IsPackaged;

    public async Task<AutoStartState> GetStateAsync()
    {
        if (!IsPackaged) return ReadRunKey() ? AutoStartState.Enabled : AutoStartState.Disabled;

        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            return Translate(task.State);
        }
        catch (Exception ex)
        {
            // Most likely the manifest carries no matching task.
            DebugLog.Write($"autostart: could not read startup task: {ex.Message}");
            return AutoStartState.Unavailable;
        }
    }

    /// <summary>
    /// Applies the request and reports what Windows actually did, which is not always
    /// what was asked.
    /// </summary>
    public async Task<AutoStartState> SetEnabledAsync(bool enabled)
    {
        if (!IsPackaged)
        {
            WriteRunKey(enabled);
            return enabled ? AutoStartState.Enabled : AutoStartState.Disabled;
        }

        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);

            if (!enabled)
            {
                task.Disable();
                DebugLog.Write("autostart: startup task disabled");
                return AutoStartState.Disabled;
            }

            var state = await task.RequestEnableAsync();
            DebugLog.Write($"autostart: requested enable, Windows returned {state}");
            return Translate(state);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"autostart: startup task change failed: {ex.Message}");
            return AutoStartState.Unavailable;
        }
    }

    private static AutoStartState Translate(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => AutoStartState.Enabled,
        StartupTaskState.DisabledByUser => AutoStartState.BlockedByUser,
        StartupTaskState.DisabledByPolicy => AutoStartState.BlockedByPolicy,
        _ => AutoStartState.Disabled,
    };

    // ---- Run key (unpackaged) ----------------------------------------------

    private static bool ReadRunKey()
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

    private static void WriteRunKey(bool enabled)
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
