using System.Diagnostics;
using System.Windows;
using Barline.Diagnostics;

namespace Barline.Platform;

/// <summary>
/// Restarts the app: start a second copy, then stand the current one down.
/// </summary>
/// <remarks>
/// <para>
/// One route for both builds. The packaged build used to ask the app model instead, on
/// the reasoning that Windows owns a packaged process's lifetime and should not have a
/// second copy started behind its back. That reasoning was sound and the API did not
/// honor it: measured in a registered package,
/// <c>CoreApplication.RequestRestartAsync</c> terminated the process and never brought
/// it back, and it never returned either, so there was no failure to report and nothing
/// to fall back to. It is documented for UWP, and a full trust Win32 app has no core
/// application view for it to restart.
/// </para>
/// <para>
/// Starting the executable again works in both builds, and a child started from inside
/// the package inherits its identity: measured, the successor read the package's own
/// local folder and asked the Store its own license question, rather than coming up as
/// a portable copy on a different data folder. That part was worth measuring instead of
/// assuming, since getting it wrong would look to the user like their settings had been
/// thrown away.
/// </para>
/// <para>
/// The order is the other half of it. Starting first means a failure leaves the app
/// running, so the caller still has something to report from; shutting down first would
/// take the app away and only then discover there was nothing to replace it with. That
/// does leave two processes alive for a moment, which is what the wait in
/// <c>App.OnStartup</c> is for: without it the successor reaches the single-instance
/// check while the old process still holds the lock, refuses itself and exits, and the
/// restart is indistinguishable from quitting.
/// </para>
/// </remarks>
internal static class AppRestart
{
    /// <summary>
    /// Restarts the app.
    /// </summary>
    /// <returns>
    /// False when the successor could not be started, in which case nothing has been
    /// shut down and this process is still running.
    /// </returns>
    public static bool TryRestart()
    {
        try
        {
            if (Environment.ProcessPath is not { } path)
            {
                DebugLog.Write("restart: no process path to start");
                return false;
            }

            // UseShellExecute off so the successor inherits this process's environment,
            // which is what carries the developer switches across a restart.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });

            Application.Current.Shutdown();

            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"restart: could not start a new instance: {ex.Message}");
            return false;
        }
    }
}
