using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Barline.Diagnostics;

namespace Barline.Platform;

/// <summary>
/// Brings the app back after an update closes it.
/// </summary>
/// <remarks>
/// <para>
/// Installing an update replaces the package this process runs from, so the installer
/// shuts it down first, and nothing started it again: observed going from 2.2.0 to
/// 2.3.0 through the Store, the widget simply went away. <c>RegisterApplicationRestart</c>
/// is how a process asks to be started again after being shut down for an update.
/// </para>
/// <para>
/// What it starts is the executable with a command line, not an activation of the app,
/// and a process started that way may come up without package identity. That matters
/// because identity decides where the app's data lives, and a Barline that came back
/// without it would read the portable data folder and look like it had thrown its
/// owner's settings away. So the command line carries the app's own user model id,
/// and a relaunch that finds itself without identity hands over to the app model with
/// it and exits, before it has touched any data. The app model always starts the
/// packaged app.
/// </para>
/// <para>
/// Registered at startup rather than just before installing, so an update the Store
/// applies on its own is covered by the same thing. Crashes, hangs and reboots are
/// excluded: a crash loop is worse than a crash, and a reboot is what the startup task
/// is for.
/// </para>
/// </remarks>
internal static class UpdateRelaunch
{
    /// <summary>The argument a relaunch is started with, followed by the id.</summary>
    private const string Switch = "--relaunch";

    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;
    private const int RestartNoReboot = 8;

    private const int ErrorSuccess = 0;

    /// <summary>
    /// What a user model id looks like, and all it is allowed to look like, since the
    /// command line this is read from can be typed by anybody.
    /// </summary>
    private static readonly Regex AppUserModelId =
        new(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}![A-Za-z0-9.\-]+$", RegexOptions.CultureInvariant);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentApplicationUserModelId(
        ref int length, StringBuilder? id);

    /// <summary>
    /// Asks Windows to start the app again if an update shuts it down.
    /// </summary>
    public static void Register()
    {
        if (!PackageContext.IsPackaged) return;

        if (CurrentId() is not { } id)
        {
            DebugLog.Write("relaunch: no app user model id to register");
            return;
        }

        int result = RegisterApplicationRestart(
            $"{Switch} {id}", RestartNoCrash | RestartNoHang | RestartNoReboot);

        DebugLog.Write(result == ErrorSuccess
            ? $"relaunch: registered for {id}"
            : $"relaunch: registration failed (0x{result:X8})");
    }

    /// <summary>
    /// Hands a relaunch that came back without package identity over to the app model.
    /// </summary>
    /// <returns>True when it did, and this process should exit without doing anything.</returns>
    public static bool HandOver(string[] args)
    {
        if (args.Length < 2 || args[0] != Switch) return false;

        DebugLog.Write($"relaunch: started after an update, packaged={PackageContext.IsPackaged}");

        if (PackageContext.IsPackaged) return false;

        var id = args[1];

        if (!AppUserModelId.IsMatch(id))
        {
            DebugLog.Write("relaunch: ignoring a malformed app user model id");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{id}")
            {
                UseShellExecute = false,
            });

            DebugLog.Write("relaunch: handed over to the app model");
            return true;
        }
        catch (Exception ex)
        {
            // Carrying on would start a portable copy on the wrong data, which is the
            // one outcome this exists to prevent. Exiting leaves the app closed, which
            // is no worse than before any of this was here.
            DebugLog.Write($"relaunch: could not hand over: {ex.Message}");
            return true;
        }
    }

    private static string? CurrentId()
    {
        int length = 0;
        GetCurrentApplicationUserModelId(ref length, null);

        if (length == 0) return null;

        var id = new StringBuilder(length);

        return GetCurrentApplicationUserModelId(ref length, id) == ErrorSuccess
            ? id.ToString()
            : null;
    }
}
