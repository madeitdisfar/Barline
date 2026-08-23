using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Barline.Diagnostics;
using Windows.Services.Store;

namespace Barline.Platform;

/// <summary>What came of asking the Store to install an update.</summary>
internal enum UpdateOutcome
{
    /// <summary>The install began. The app is closed to finish it.</summary>
    Started,

    /// <summary>There was nothing to install after all.</summary>
    NothingToDo,

    /// <summary>The user closed the Store's dialog without installing.</summary>
    Canceled,

    /// <summary>The Store could not be reached or would not answer.</summary>
    Failed,
}

/// <summary>
/// Whether a newer Barline is waiting in the Store, and installing it.
/// </summary>
/// <remarks>
/// <para>
/// The Store updates apps on its own, but it cannot replace a package whose processes
/// are running, and Barline's process is running from sign-in until shutdown. Observed
/// on a machine a release had been published to: the silent update never applied while
/// the widget was up, and driving it by hand from the Store finished the download,
/// offered a Retry, and only completed once that Retry closed Barline. The app did not
/// come back.
/// </para>
/// <para>
/// So the update stays pending until the machine is restarted, which on a laptop that
/// only ever sleeps can be weeks. Rather than depend on knowing exactly what the Store
/// will do unattended, the app asks whether an update is waiting and offers to install
/// it, which puts the timing and the wording in the app's hands and makes the question
/// moot.
/// </para>
/// <para>
/// Nothing here runs on an unpackaged build. There is no Store to ask, and a portable
/// copy is updated by replacing it.
/// </para>
/// </remarks>
internal sealed class StoreUpdates
{
    /// <summary>
    /// Asks Windows to start this app again if it is shut down to be patched.
    /// </summary>
    /// <remarks>
    /// Registered just before the install, because that is the shutdown it is for. It
    /// is the installer that decides whether to honor it, so nothing the user is told
    /// promises a relaunch: the copy says the app will close, and coming back by itself
    /// is a bonus rather than a claim. Failing here is not worth reporting, since the
    /// worst case is the app the user has to start again, which is the case anyway.
    /// </remarks>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    /// <summary>Restart for patching and reboots, but not for crashes or hangs.</summary>
    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;

    /// <summary>Whether a newer version is waiting.</summary>
    public bool Available { get; private set; }

    /// <summary>The version that is waiting, for the wording that names it.</summary>
    public string? Version { get; private set; }

    /// <summary>Raised when the answer changes, never when it is merely re-confirmed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Asks the Store whether there is an update, and remembers the answer.
    /// </summary>
    /// <param name="owner">
    /// A window for the Store context, which a desktop process must supply before the
    /// context will work at all.
    /// </param>
    public async Task CheckAsync(IntPtr owner)
    {
        if (Override() is { } forced)
        {
            Publish(true, forced);
            return;
        }

        if (!PackageContext.IsPackaged) return;

        try
        {
            var updates = await UpdatesAsync(owner);

            Publish(updates.Count > 0, Newest(updates));
        }
        catch (Exception ex)
        {
            // An unreachable Store is an ordinary condition, not a fault to report. The
            // answer keeps whatever it was: an update that was waiting an hour ago is
            // still waiting whether or not the Store can be asked about it now.
            DebugLog.Write($"updates: could not ask the Store: {ex.Message}");
        }
    }

    /// <summary>
    /// Installs whatever is waiting. The app is closed part way through this.
    /// </summary>
    /// <remarks>
    /// The list is fetched again rather than kept from the check. These are WinRT
    /// objects describing packages on a server, and the interesting case for this
    /// method is the one where the check ran hours ago.
    /// </remarks>
    public async Task<UpdateOutcome> InstallAsync(IntPtr owner)
    {
        if (!PackageContext.IsPackaged) return UpdateOutcome.NothingToDo;

        try
        {
            var context = ContextFor(owner);
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();

            if (updates.Count == 0)
            {
                Publish(false, null);
                return UpdateOutcome.NothingToDo;
            }

            RegisterApplicationRestart(null, RestartNoCrash | RestartNoHang);

            var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);

            DebugLog.Write($"updates: install ended as {result.OverallState}");

            return result.OverallState switch
            {
                StorePackageUpdateState.Completed => UpdateOutcome.Started,

                // Reached only if the app is somehow still alive: the install ends by
                // replacing the package this process is running from.
                StorePackageUpdateState.Canceled => UpdateOutcome.Canceled,

                _ => UpdateOutcome.Failed,
            };
        }
        catch (Exception ex)
        {
            DebugLog.Write($"updates: install failed: {ex.Message}");
            return UpdateOutcome.Failed;
        }
    }

    private static async Task<IReadOnlyList<StorePackageUpdate>> UpdatesAsync(IntPtr owner) =>
        await ContextFor(owner).GetAppAndOptionalStorePackageUpdatesAsync();

    /// <summary>The highest version on offer, written the way a person reads one.</summary>
    /// <remarks>
    /// Highest rather than first, because the list can hold optional packages as well
    /// as the app itself, and what the user is being told about is the app.
    /// </remarks>
    private static string? Newest(IReadOnlyList<StorePackageUpdate> updates)
    {
        string? newest = null;
        var highest = new Version(0, 0, 0);

        foreach (var update in updates)
        {
            var id = update.Package.Id.Version;
            var version = new Version(id.Major, id.Minor, id.Build);

            if (version <= highest) continue;

            highest = version;
            newest = version.ToString();
        }

        return newest;
    }

    private void Publish(bool available, string? version)
    {
        if (available == Available && version == Version) return;

        DebugLog.Write($"updates: available={available} version={version ?? "?"}");

        Available = available;
        Version = version;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A version to pretend is waiting, so the surfaces can be looked at.
    /// </summary>
    /// <remarks>
    /// The alternative is publishing a release to the Store and waiting for it to reach
    /// this machine, which is not a way to iterate on a card and a badge. Read through
    /// <see cref="DevOverride"/>, so a packaged build ignores it.
    /// </remarks>
    private static string? Override() => DevOverride.Read("BARLINE_UPDATE");

    /// <summary>
    /// A context tied to a window, which is the only kind a desktop process may use.
    /// </summary>
    private static StoreContext ContextFor(IntPtr owner)
    {
        var context = StoreContext.GetDefault();

        WinRT.Interop.InitializeWithWindow.Initialize(context, owner);

        return context;
    }
}
