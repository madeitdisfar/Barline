using System.Runtime.InteropServices;
using Barline.Diagnostics;

namespace Barline.Platform;

/// <summary>
/// Whether this process is running from an MSIX package.
/// </summary>
/// <remarks>
/// The same binary ships two ways, and several things differ between them: where
/// user data belongs, how starting with Windows is registered, and whether the Store
/// APIs exist at all. One answer, resolved once, so those decisions cannot disagree
/// with each other.
/// </remarks>
internal static class PackageContext
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    /// <summary>True when the process has package identity.</summary>
    public static bool IsPackaged { get; } = Detect();

    /// <summary>
    /// Asks Windows for this process's package identity. Querying rather than
    /// catching the exception <c>Package.Current</c> throws, which is the same answer
    /// obtained by more expensive means.
    /// </summary>
    private static bool Detect()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"package detection failed: {ex.Message}");
            return false;
        }
    }
}
