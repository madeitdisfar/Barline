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
/// Where the current lyric line is drawn.
/// </summary>
internal enum LyricsDisplayMode
{
    /// <summary>
    /// In the widget's own text area, replacing the title until hovered. Costs no
    /// extra window and no extra pixels, but the reserved width is about 150px —
    /// twenty-five characters — so a long line is cut short.
    /// </summary>
    Inline,

    /// <summary>
    /// A panel floating just above the taskbar, with room for the line before and
    /// after. Reads far better; costs a second window.
    /// </summary>
    Panel,
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

    /// <summary>Where lyrics are drawn, when they are enabled.</summary>
    public LyricsDisplayMode LyricsDisplay { get; set; } = LyricsDisplayMode.Inline;

    /// <summary>
    /// Whether to look up lyrics for what is playing.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately so. A lookup sends the track's title and
    /// artist to a third-party service, which is not something the widget should
    /// start doing without being asked.
    /// </remarks>
    public bool LyricsEnabled { get; set; }

    /// <summary>The bar count when nothing says otherwise, and the shipped design.</summary>
    public const int DefaultBarCount = 4;

    /// <summary>
    /// Narrowest and widest bar counts on offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling is six for two independent reasons, either of which would set it
    /// there alone. Visually, the bars divide a fixed width, so seven bars are 1.7px
    /// wide and the tall neighbouring ones visibly merge at 100% scaling — worse on
    /// the light taskbar, where the bar colour is translucent as well as thin.
    /// </para>
    /// <para>
    /// Technically, a 1024-point FFT at 48kHz resolves 46.9Hz per bin, and the
    /// lowest band spans 40Hz to <c>40 × 256^(1/n)</c>. At seven bands that upper
    /// edge is 89.8Hz, which falls in the same bin the second band starts from, so
    /// the lowest band becomes a strict subset of its neighbour and the two bottom
    /// bars move as one.
    /// </para>
    /// </remarks>
    public const int MinBarCount = 4;
    public const int MaxBarCount = 6;

    /// <summary>
    /// How many bars the visualiser draws. Bars share a fixed width and ink budget,
    /// so this trades detail for thickness and never widens the widget.
    /// </summary>
    public int VisualizerBarCount { get; set; } = DefaultBarCount;

    /// <summary>
    /// Forces hand-edited values back into range.
    /// </summary>
    /// <remarks>
    /// The file is documented as safe to edit, so out-of-range values are an
    /// expected input rather than a corrupt one. Clamping beats rejecting the whole
    /// file: a mistyped bar count should not also discard the user's colour.
    /// </remarks>
    public void Normalize() =>
        VisualizerBarCount = Math.Clamp(VisualizerBarCount, MinBarCount, MaxBarCount);
}
