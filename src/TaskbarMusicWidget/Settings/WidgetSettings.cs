namespace TaskbarMusicWidget.Settings;

/// <summary>
/// Where the visualiser's bar colour comes from.
/// </summary>
/// <remarks>
/// Serialised by name, not by number, so the file stays readable and reordering
/// this enum can never silently repoint an existing setting.
/// </remarks>
internal enum VisualizerColorMode
{
    /// <summary>
    /// The built-in theme colour — white on the dark taskbar, a medium grey on the
    /// light one. The only mode that needs no legibility correction, because both
    /// values are chosen against the taskbar material by hand.
    /// </summary>
    Default,

    /// <summary>The user's Windows accent colour, corrected for legibility.</summary>
    SystemAccent,

    /// <summary>A fixed colour from <see cref="WidgetSettings.CustomBarColor"/>.</summary>
    Custom,

    /// <summary>The dominant hue of the current album art, corrected for legibility.</summary>
    AlbumArt,
}

/// <summary>
/// User settings, persisted as JSON. A plain mutable model: it is the shape of the
/// file, and later the backing object for the settings window.
/// </summary>
/// <remarks>
/// Every property must have a usable default, because a missing or malformed file
/// is a normal condition (first run, hand-edited mistake) and must never stop the
/// widget from starting. Mutate only through <see cref="SettingsStore.Update"/>, so
/// saving and change notification cannot drift apart from the value.
/// </remarks>
internal sealed class WidgetSettings
{
    /// <summary>
    /// Schema version, for migrating files written by older builds. Bump it only
    /// when an existing property changes meaning — adding a property does not need
    /// a bump, since absent values fall back to the defaults here.
    /// </summary>
    public int Version { get; set; } = 1;

    public VisualizerColorMode VisualizerColor { get; set; } = VisualizerColorMode.Default;

    /// <summary>
    /// Colour for <see cref="VisualizerColorMode.Custom"/>, as <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// Still corrected for legibility against the taskbar, so a colour picked here
    /// is a request for a hue rather than for exact bytes. Until the settings window
    /// exists this is only reachable by editing the file.
    /// </remarks>
    public string? CustomBarColor { get; set; }

    /// <summary>
    /// Whether the bars are drawn at all. Previously in-memory only, so turning the
    /// visualiser off did not survive a restart.
    /// </summary>
    public bool VisualizerEnabled { get; set; } = true;
}
