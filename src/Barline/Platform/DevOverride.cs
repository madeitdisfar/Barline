namespace Barline.Platform;

/// <summary>
/// Environment variables that exist to make the app easier to work on, and that a
/// shipped build has no business honoring.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is read through here rather than from
/// <see cref="Environment.GetEnvironmentVariable(string)"/> directly, so the packaged
/// build cannot honor one by omission. Adding a new switch and forgetting to gate it
/// is the failure this shape prevents.
/// </para>
/// <para>
/// The reason is <c>BARLINE_LICENSE</c>, which forces the license state. Left live in
/// the Store build it would put "unlock everything" one <c>setx</c> away, since a
/// user-level variable is inherited by anything Explorer launches. That is not the
/// same as the gate being removable by recompiling, which the GPL makes legal and
/// expected: rebuilding the product is a different act from pasting one command out of
/// a forum post, and this one is documented by name in the repository.
/// </para>
/// <para>
/// The others grant nothing paid, but a Store build has no use for a synthetic track
/// or a window that says thank you for a purchase nobody made, so they are gated
/// together rather than one by one on merit.
/// </para>
/// <para>
/// <c>BARLINE_DEBUG</c> is deliberately **not** here. It writes a local diagnostic log
/// and is the only way to find out what went wrong on somebody else's machine, which
/// is exactly the build where that matters. It is also promised to Store users by the
/// privacy policy, which describes the file it produces.
/// </para>
/// </remarks>
internal static class DevOverride
{
    /// <summary>The value, or null in a packaged build whatever the environment says.</summary>
    public static string? Read(string name) =>
        PackageContext.IsPackaged ? null : Environment.GetEnvironmentVariable(name);

    /// <summary>Whether the switch is present at all, regardless of its value.</summary>
    public static bool IsSet(string name) => Read(name) is not null;

    /// <summary>Whether the switch is set to the usual "on" value.</summary>
    public static bool IsOn(string name) => Read(name) == "1";
}
