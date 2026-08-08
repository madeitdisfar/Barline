using System.IO;
using Barline.Diagnostics;

namespace Barline.Platform;

/// <summary>
/// Where settings, presets and cached lyrics are kept.
/// </summary>
/// <remarks>
/// <para>
/// Two roots, because the two builds are uninstalled differently. Unpackaged, this is
/// <c>%LocalAppData%\Barline</c> and removing the app is deleting a folder, so leaving
/// data behind is the user's to clean up and always has been.
/// </para>
/// <para>
/// Packaged, the root is the package's own local folder. Windows deletes that on
/// uninstall, which is the contract a packaged app is expected to honour; writing to
/// <c>%LocalAppData%\Barline</c> from a package would leave a folder behind that
/// nothing owns and nothing removes. The path is longer and less memorable, which is
/// why the settings window opens these folders rather than printing them.
/// </para>
/// <para>
/// One consequence worth knowing: the two builds do not share state. Installing the
/// packaged build alongside the portable one starts from defaults rather than
/// inheriting its settings, which is the same way any two separate installations
/// behave.
/// </para>
/// </remarks>
internal static class AppPaths
{
    /// <summary>The folder everything else sits under.</summary>
    public static string Root { get; } = ResolveRoot();

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string Presets => Path.Combine(Root, "presets");

    public static string LyricsCache => Path.Combine(Root, "lyrics");

    private static string ResolveRoot()
    {
        if (PackageContext.IsPackaged)
        {
            try
            {
                // Asked for rather than assembled from the package family name: the
                // layout under Packages\ is Windows' to define, not ours to predict.
                return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception ex)
            {
                // Should not happen with package identity, but falling back to a
                // working folder beats failing to start over where to put a file.
                DebugLog.Write($"package local folder unavailable: {ex.Message}");
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Barline");
    }
}
