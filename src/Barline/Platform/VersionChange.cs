using Barline.Diagnostics;
using Barline.Settings;

namespace Barline.Platform;

/// <summary>
/// Whether this run is the first one after an update.
/// </summary>
/// <remarks>
/// <para>
/// Installing an update closes the app, so from the taskbar it looks like the widget
/// vanished and, some time later, came back. Saying what happened is the point of
/// this: not a changelog, and not a boast, but an answer to the disappearance somebody
/// just watched.
/// </para>
/// <para>
/// Resolved once, at startup, and the file is written straight away. The answer lives
/// for that session and no longer: somebody who does not open the settings window
/// before the next restart never hears about it, which is the right amount of
/// insistence for news that keeps for nobody.
/// </para>
/// </remarks>
internal sealed class VersionChange
{
    public VersionChange(SettingsStore settings)
    {
        Previous = settings.Current.LastRunVersion;
        Updated = IsUpdate(Previous, AppInfo.Version);

        if (Previous == AppInfo.Version) return;

        DebugLog.Write($"version: {Previous ?? "first run"} -> {AppInfo.Version}");

        settings.Update(s => s.LastRunVersion = AppInfo.Version);
    }

    /// <summary>The version that ran last, or null on a first run.</summary>
    public string? Previous { get; }

    /// <summary>Whether this run follows an update of the app.</summary>
    public bool Updated { get; }

    /// <summary>
    /// Whether going from one version to the other counts as an update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A first run is not one. There is nothing to have updated from, and the welcome
    /// window is already talking; a second greeting beside it would be one too many.
    /// </para>
    /// <para>
    /// Nor is going back. Rolling a build back is something a developer does on
    /// purpose, and it is not news to announce to them, so only a genuine increase
    /// counts. Anything that will not parse is not compared at all.
    /// </para>
    /// </remarks>
    internal static bool IsUpdate(string? previous, string current) =>
        Version.TryParse(previous, out var before) &&
        Version.TryParse(current, out var now) &&
        now > before;
}
