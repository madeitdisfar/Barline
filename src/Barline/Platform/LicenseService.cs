using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Barline.Diagnostics;
using Windows.Services.Store;

namespace Barline.Platform;

/// <summary>
/// What the Store was able to tell us about the add-on.
/// </summary>
/// <remarks>
/// Three states rather than a bool, because "no" and "could not ask" have to be told
/// apart. They lead to the same features being unavailable but to opposite handling of
/// the user's file: a real no is grounds for stripping paid values out of it, and a
/// failed question never is. Collapsing them is how an outage turns into data loss.
/// </remarks>
internal enum LicenseState
{
    /// <summary>The Store could not be asked, or did not answer.</summary>
    Unknown,

    /// <summary>The add-on is owned.</summary>
    Licensed,

    /// <summary>The Store answered, and the add-on is not owned.</summary>
    NotLicensed,
}

/// <summary>
/// How a purchase attempt ended.
/// </summary>
/// <remarks>
/// The failures are kept apart because they ask the user for different things. Windows
/// draws its own dialog for anything that goes wrong inside the transaction — a
/// declined card, a missing payment method — so what reaches here is the call itself
/// failing, and "try again later" is the wrong advice for a machine that is offline.
/// </remarks>
internal enum PurchaseOutcome
{
    Bought,
    AlreadyOwned,

    /// <summary>The dialog was closed without buying.</summary>
    Canceled,

    /// <summary>The Store could not be reached at all.</summary>
    NoNetwork,

    /// <summary>The Store was reached and something went wrong at its end.</summary>
    StoreBusy,

    /// <summary>The call itself failed, which usually means a broken Store client.</summary>
    Failed,

    /// <summary>There is no Store to buy from, which is every unpackaged build.</summary>
    Unavailable,
}

/// <summary>
/// Whether the paid features are available, and whether that answer is trustworthy
/// enough to act on destructively.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately one plain bool behind one plain check, with no obfuscation. The source
/// is GPL-3.0, so anyone who wants to remove this can, legally, by compiling — and the
/// portable build hands the paid features over for free anyway. What is being sold is
/// the packaged app: updates, a sandboxed install, and not doing any of that. Spending
/// effort on hiding the gate would only make the code worse for the people who paid.
/// </para>
/// <para>
/// Resolved in two phases. The constructor answers from what is already on disk, so
/// startup never waits on the Store, and <see cref="RefreshAsync"/> asks it once a
/// window exists to own the call. Both are needed: <c>StoreContext</c> refuses to work
/// in a desktop process until it has been handed a window handle, and there is no
/// window at the point the settings are first read.
/// </para>
/// </remarks>
internal sealed class LicenseService
{
    /// <summary>The add-on's product ID, as typed into Partner Center.</summary>
    /// <remarks>
    /// Matched against <c>InAppOfferToken</c> rather than the Store ID, because that is
    /// the field a durable add-on's license carries.
    /// </remarks>
    private const string ProductId = "barline-plus";

    /// <summary>The add-on's Store ID, which is what a purchase is requested by.</summary>
    private const string StoreId = "9NBHM90NFZQR";

    /// <summary>What the add-on is called to the user.</summary>
    /// <remarks>
    /// Referenced by every string that names it rather than being spelled out in each,
    /// so the UI cannot end up half renamed.
    /// </remarks>
    public const string ProductName = "Barline Plus";

    /// <summary>
    /// How long a remembered "yes" keeps working when the Store cannot be reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows caches Store licenses locally and they survive being offline on their
    /// own, so this is for service faults rather than for flights.
    /// </para>
    /// <para>
    /// Long on purpose, because it guards one narrow case and nothing else: an owner
    /// this install has already confirmed, whose Store then stays unreachable. Everyone
    /// we have never confirmed — every free user, and an owner on a fresh install or a
    /// new machine — reaches <see cref="LicenseState.Unknown"/> without ever consulting
    /// this, which is what keeps the safety property from depending on it.
    /// </para>
    /// <para>
    /// Expiring at all only ever costs a paying customer. It does not guard against
    /// refunds: a refund is a positive no the moment the Store can be asked, and that
    /// deletes the memory outright rather than waiting for it to lapse.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan Grace = TimeSpan.FromDays(365);

    private readonly string _path = Path.Combine(AppPaths.Root, "license.json");

    public LicenseService()
    {
        State = Remembered();
    }

    /// <summary>The state everything is currently gated on.</summary>
    /// <remarks>
    /// Changed only by <see cref="RefreshAsync"/> and <see cref="PurchaseAsync"/>, both
    /// of which report whether it moved so the caller can re-apply the consequences.
    /// Nothing else may set it, or the window could end up gated one way while the
    /// settings file was rewritten the other.
    /// </remarks>
    public LicenseState State { get; private set; }

    /// <summary>Whether the paid features are available.</summary>
    public bool Premium => State == LicenseState.Licensed;

    /// <summary>
    /// Whether there is an add-on to talk about at all.
    /// </summary>
    /// <remarks>
    /// False for the portable build, which owns nothing and can buy nothing, so the
    /// settings window hides the whole section rather than telling somebody they have
    /// unlocked a purchase they never made. A forced state counts as having one, or the
    /// only way to look at that half of the window would be to package a build.
    /// </remarks>
    public static bool Sellable => PackageContext.IsPackaged || Override() is not null;

    /// <summary>
    /// Whether paid values may be taken out of the user's settings.
    /// </summary>
    /// <remarks>
    /// Only ever true on a positive no. <see cref="LicenseState.Unknown"/> denies the
    /// features for this run, which is recoverable, but never touches the file, which
    /// would not be.
    /// </remarks>
    public bool MayStrip => State == LicenseState.NotLicensed;

    /// <summary>
    /// The state before the Store has been asked: what we already knew, and nothing
    /// more.
    /// </summary>
    /// <remarks>
    /// Never <see cref="LicenseState.NotLicensed"/>. Not having asked is not an answer,
    /// and treating it as one here would strip a first launch before the license had
    /// even been looked up.
    /// </remarks>
    private LicenseState Remembered()
    {
        if (Override() is { } forced) return forced;

        // Nothing to ask, and nobody to ask it of. A build from source is the source,
        // and gating it would only punish the people the license is written for.
        if (!PackageContext.IsPackaged) return LicenseState.Licensed;

        return WithinGrace() ? LicenseState.Licensed : LicenseState.Unknown;
    }

    /// <summary>
    /// Asks the Store, now that there is a window to own the call.
    /// </summary>
    /// <param name="owner">A window handle belonging to this process.</param>
    /// <returns>True when the answer differs from what we were working on.</returns>
    public async Task<bool> RefreshAsync(IntPtr owner)
    {
        if (Override() is not null) return false;
        if (!PackageContext.IsPackaged) return false;

        var answer = await AskAsync(owner);

        if (answer is LicenseState.Unknown)
        {
            // Keep whatever the remembered state gave us. An outage must not demote
            // somebody the grace period had already vouched for.
            return false;
        }

        if (answer is LicenseState.Licensed) Remember();
        else Forget();

        if (answer == State) return false;

        DebugLog.Write($"license: {State} -> {answer}");
        State = answer;

        return true;
    }

    /// <summary>
    /// Puts the Store's purchase dialog up, and takes the result as an answer.
    /// </summary>
    /// <remarks>
    /// A cancelled purchase is deliberately not treated as a positive no. The user
    /// declining a dialog says nothing about what they own, and letting it set
    /// <see cref="LicenseState.NotLicensed"/> would make closing a window strip the
    /// settings of somebody who owns the add-on on another machine.
    /// </remarks>
    public async Task<PurchaseOutcome> PurchaseAsync(IntPtr owner)
    {
        if (!PackageContext.IsPackaged) return PurchaseOutcome.Unavailable;

        try
        {
            var context = ContextFor(owner);
            var result = await context.RequestPurchaseAsync(StoreId);

            switch (result.Status)
            {
                case StorePurchaseStatus.Succeeded:
                case StorePurchaseStatus.AlreadyPurchased:
                    Remember();
                    State = LicenseState.Licensed;
                    return result.Status == StorePurchaseStatus.Succeeded
                        ? PurchaseOutcome.Bought
                        : PurchaseOutcome.AlreadyOwned;

                case StorePurchaseStatus.NotPurchased:
                    return PurchaseOutcome.Canceled;

                case StorePurchaseStatus.NetworkError:
                    DebugLog.Write("license: purchase could not reach the Store");
                    return PurchaseOutcome.NoNetwork;

                case StorePurchaseStatus.ServerError:
                    DebugLog.Write("license: the Store failed the purchase");
                    return PurchaseOutcome.StoreBusy;

                default:
                    DebugLog.Write($"license: purchase failed with {result.Status}");
                    return PurchaseOutcome.Failed;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"license: purchase threw: {ex.Message}");
            return PurchaseOutcome.Failed;
        }
    }

    /// <summary>
    /// Reads the add-on's license out of the app's.
    /// </summary>
    /// <remarks>
    /// Every failure path returns <see cref="LicenseState.Unknown"/> rather than a no.
    /// A thrown call means the question did not get asked, and the one thing that must
    /// never happen is a Store fault reading as "they did not buy it".
    /// </remarks>
    private static async Task<LicenseState> AskAsync(IntPtr owner)
    {
        try
        {
            var context = ContextFor(owner);
            var license = await context.GetAppLicenseAsync();

            if (license is null) return LicenseState.Unknown;

            foreach (var addOn in license.AddOnLicenses.Values)
                if (addOn.InAppOfferToken == ProductId && addOn.IsActive)
                    return LicenseState.Licensed;

            return LicenseState.NotLicensed;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"license: could not ask the Store: {ex.Message}");
            return LicenseState.Unknown;
        }
    }

    /// <summary>
    /// A context tied to a window.
    /// </summary>
    /// <remarks>
    /// Not optional. <c>StoreContext</c> has to be told which window owns the modal
    /// dialogs it may raise, and in a desktop process it throws rather than picking one
    /// itself. Done for every call, since the owning window differs — startup uses the
    /// widget, a purchase uses the settings window it was clicked in.
    /// </remarks>
    private static StoreContext ContextFor(IntPtr owner)
    {
        var context = StoreContext.GetDefault();

        WinRT.Interop.InitializeWithWindow.Initialize(context, owner);

        return context;
    }

    /// <summary>
    /// Forces a state, for looking at the other half of the UI.
    /// </summary>
    /// <remarks>
    /// <c>free</c> is deliberately <see cref="LicenseState.Unknown"/> rather than a no,
    /// because that is the state that locks the window without touching the settings
    /// file. Reaching the locked UI should not cost the developer their own
    /// configuration. <c>none</c> is the real thing, stripping included, and exists to
    /// test that path on purpose. Any of them also stops the Store being asked, so a
    /// forced state stays forced.
    /// </remarks>
    private static LicenseState? Override() =>
        Environment.GetEnvironmentVariable("BARLINE_LICENSE") switch
        {
            "owned" => LicenseState.Licensed,
            "free" => LicenseState.Unknown,
            "none" => LicenseState.NotLicensed,
            _ => null,
        };

    private bool WithinGrace()
    {
        try
        {
            if (!File.Exists(_path)) return false;

            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(_path));

            if (record?.LastSeen is not { } seen) return false;

            // A clock that has gone backwards must not extend the grace indefinitely,
            // so anything in the future is treated as now.
            var age = DateTimeOffset.UtcNow - seen;

            return age <= Grace;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"license: could not read the remembered state: {ex.Message}");
            return false;
        }
    }

    private void Remember()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);

            string json = JsonSerializer.Serialize(
                new Record { LastSeen = DateTimeOffset.UtcNow },
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"license: could not remember the state: {ex.Message}");
        }
    }

    private void Forget()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"license: could not clear the remembered state: {ex.Message}");
        }
    }

    /// <summary>
    /// The remembered answer. Only ever written after a yes, so its presence is the
    /// claim and its age is the whole of the check.
    /// </summary>
    private sealed class Record
    {
        public DateTimeOffset? LastSeen { get; set; }
    }
}
