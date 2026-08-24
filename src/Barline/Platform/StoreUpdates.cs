using System.Threading.Tasks;
using Barline.Diagnostics;
using Windows.Services.Store;

namespace Barline.Platform;

/// <summary>How far along an install is, and which half of it.</summary>
/// <param name="Fraction">0 to 1 through the phase named by <paramref name="Installing"/>.</param>
/// <param name="Installing">False while downloading, true once installing.</param>
internal readonly record struct UpdateProgress(double Fraction, bool Installing);

/// <summary>What came of asking the Store to install an update.</summary>
internal enum UpdateOutcome
{
    /// <summary>
    /// The install finished. Usually unreachable, the app having been closed to get
    /// there, and worth acting on when it is reached: see <see cref="StoreUpdates"/>.
    /// </summary>
    Installed,

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
/// Nothing here promises the app comes back. The installer closes it to replace the
/// package it is running from, and whether anything starts it again is the installer's
/// decision: Microsoft's own sample calls that step <c>IsNowAGoodTimeToRestartApp</c>
/// and warns that installing "may cause the application to exit".
/// <c>RegisterApplicationRestart</c> was tried and taken out again. It asks Windows to
/// run the executable's command line afresh, which is not the case
/// <see cref="AppRestart"/> measured: that one works because the successor is a child
/// and inherits this process's package identity. A fresh launch has nothing to inherit,
/// and a Barline that came back without identity would read the portable data folder
/// and look to its owner like it had thrown their settings away. Not worth risking for
/// a relaunch that may not happen anyway.
/// </para>
/// <para>
/// What is worth doing is the case where the install finishes and this process is
/// somehow still alive, which leaves the old code running against a replaced package.
/// <see cref="AppRestart"/> covers that one properly, and the caller uses it.
/// </para>
/// <para>
/// Nothing here runs on an unpackaged build. There is no Store to ask, and a portable
/// copy is updated by replacing it.
/// </para>
/// </remarks>
internal sealed class StoreUpdates
{
    /// <summary>
    /// The share of the Store's progress figure that the download accounts for.
    /// </summary>
    /// <remarks>
    /// Documented, not guessed: <c>PackageDownloadProgress</c> runs 0 to 0.8 while the
    /// package downloads and 0.8 to 1 while it installs. Shown as one bar climbing to
    /// 100% and then starting again, since those are two different waits with a dialog
    /// between them, and a bar that stops at 80% to ask a question reads as stuck.
    /// </remarks>
    private const double DownloadShare = 0.8d;

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
    /// <param name="owner">The window the Store's own dialogs belong to.</param>
    /// <param name="progress">Told how far along it is, on the calling thread.</param>
    /// <remarks>
    /// <para>
    /// Two dialogs of the OS's own bracket this, and they are the consent that matters:
    /// one asking permission to download, and one after the download asking permission
    /// to install, which warns that the app may have to restart. Declining either ends
    /// the operation as <c>Canceled</c> rather than as a failure.
    /// </para>
    /// <para>
    /// The list is fetched again rather than kept from the check. These are WinRT
    /// objects describing packages on a server, and the interesting case for this
    /// method is the one where the check ran hours ago.
    /// </para>
    /// <para>
    /// Called on the UI thread, because the Store requires it: off it, the call fails
    /// with <c>ERROR_INVALID_WINDOW_HANDLE</c> rather than with anything that names the
    /// real problem.
    /// </para>
    /// </remarks>
    public async Task<UpdateOutcome> InstallAsync(
        IntPtr owner, IProgress<UpdateProgress>? progress = null)
    {
        // An override that pretends an update is waiting has to pretend to install it
        // too, or the half of this the user actually watches could never be looked at.
        if (Override() is not null) return await PretendAsync(progress);

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

            var operation = context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);

            // Raised once per step per package, on a thread of the Store's choosing.
            // An IProgress made on the UI thread is what carries it back to one.
            operation.Progress = (_, status) =>
                progress?.Report(Describe(status.PackageDownloadProgress));

            var result = await operation;

            DebugLog.Write($"updates: install ended as {result.OverallState}");

            return result.OverallState switch
            {
                StorePackageUpdateState.Completed => UpdateOutcome.Installed,

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

    /// <summary>Splits the Store's one figure into the two waits it covers.</summary>
    internal static UpdateProgress Describe(double far) =>
        far < DownloadShare
            ? new UpdateProgress(far / DownloadShare, false)
            : new UpdateProgress((far - DownloadShare) / (1d - DownloadShare), true);

    /// <summary>Walks the bar through both phases at a believable pace.</summary>
    private static async Task<UpdateOutcome> PretendAsync(IProgress<UpdateProgress>? progress)
    {
        for (int step = 0; step <= 20; step++)
        {
            await Task.Delay(150);
            progress?.Report(Describe(step / 20d));
        }

        return UpdateOutcome.Installed;
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
