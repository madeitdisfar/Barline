using System.IO;
using System.Reflection;

namespace Barline.Platform;

/// <summary>
/// What the app says about itself: version, where its source is, and where the
/// documents it ships with ended up.
/// </summary>
/// <remarks>
/// <para>
/// Gathered in one place because these facts are stated in several: the LRCLIB user
/// agent carries the version and a link to the project, the About card shows both to
/// the user, and the licence obliges the binary to say where its source can be had.
/// Three copies of a URL is three chances for one of them to be wrong, and the one
/// that went wrong last time was the one nobody looked at.
/// </para>
/// <para>
/// The version is read from the assembly rather than written out, so it cannot drift
/// from what actually shipped. <c>Version</c> in the project file remains the single
/// place a release number is set.
/// </para>
/// </remarks>
internal static class AppInfo
{
    public const string Name = "Barline";

    /// <summary>
    /// Where the Corresponding Source lives. GPL-3.0 §6 lets the source be offered
    /// from a network server rather than shipped, provided the binary says plainly
    /// where to find it — which is what this URL is doing in the About card.
    /// </summary>
    public const string RepositoryUrl = "https://github.com/madeitdisfar/Barline";

    public const string WebsiteUrl = "https://madeitdisfar.github.io/Barline/";

    /// <summary>
    /// The same address given to the Store, so the policy someone agreed to at
    /// install time is the one the app points at afterwards.
    /// </summary>
    public const string PrivacyUrl = "https://madeitdisfar.github.io/Barline/privacy.html";

    /// <summary>Three parts. The revision is always zero and saying so helps nobody.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0";

    /// <summary>Read from the assembly so the year is not a second thing to update.</summary>
    public static string Copyright { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? string.Empty;

    /// <summary>The GPL text, laid down beside the executable by the build.</summary>
    public static string LicenseFile => BesideTheExecutable("LICENSE");

    /// <summary>Notices for the components compiled into the executable.</summary>
    public static string NoticesFile => BesideTheExecutable("THIRD-PARTY-NOTICES.md");

    /// <summary>
    /// Resolves a file that ships with the app.
    /// </summary>
    /// <remarks>
    /// <see cref="AppContext.BaseDirectory"/> rather than the assembly's own location,
    /// which is empty in a single-file build — the shape every release is published
    /// in. These two files are deliberately left outside the bundle so they can be
    /// read without running the app at all.
    /// </remarks>
    private static string BesideTheExecutable(string name) =>
        Path.Combine(AppContext.BaseDirectory, name);
}
