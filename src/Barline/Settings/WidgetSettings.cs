using System.Text.Json.Serialization;
using Barline.Lyrics;

namespace Barline.Settings;

/// <summary>
/// Where the visualizer's bar color comes from.
/// </summary>
/// <remarks>
/// Serialized by name, not by number, so the file stays readable and reordering
/// this enum can never silently repoint an existing setting.
/// </remarks>
internal enum VisualizerColorMode
{
    /// <summary>
    /// The built-in theme color — white on the dark taskbar, a medium gray on the
    /// light one. The only mode that needs no legibility correction, because both
    /// values are chosen against the taskbar material by hand.
    /// </summary>
    Default,

    /// <summary>The user's Windows accent color, corrected for legibility.</summary>
    SystemAccent,

    /// <summary>A fixed color from <see cref="WidgetSettings.CustomBarColor"/>.</summary>
    Custom,

    /// <summary>The dominant hue of the current album art, corrected for legibility.</summary>
    AlbumArt,
}

/// <summary>What the panel does while the pointer is over it.</summary>
/// <remarks>
/// The panel passes clicks through, so it never blocks anything — but it can still sit
/// on top of something you are trying to read.
/// </remarks>
internal enum LyricsHoverBehavior
{
    /// <summary>Stay as it is.</summary>
    None,

    /// <summary>Fade most of the way out, so what is underneath can be read through it.</summary>
    Fade,

    /// <summary>Get out of the way entirely until the pointer leaves.</summary>
    Hide,
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
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Version 2 collapsed the two lyric appearances into one style.</summary>
    public const int CurrentVersion = 2;

    public VisualizerColorMode VisualizerColor { get; set; } = VisualizerColorMode.Default;

    /// <summary>
    /// Color for <see cref="VisualizerColorMode.Custom"/>, as <c>#RRGGBB</c>.
    /// </summary>
    /// <remarks>
    /// Still corrected for legibility against the taskbar, so a color picked here
    /// is a request for a hue rather than for exact bytes.
    /// </remarks>
    public string? CustomBarColor { get; set; }

    /// <summary>
    /// Whether the bars are drawn at all. Previously in-memory only, so turning the
    /// visualizer off did not survive a restart.
    /// </summary>
    public bool VisualizerEnabled { get; set; } = true;

    /// <summary>
    /// Whether to look up lyrics for what is playing.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately so. A lookup sends the track's title and
    /// artist to a third-party service, which is not something the widget should
    /// start doing without being asked.
    /// </remarks>
    public bool LyricsEnabled { get; set; }

    /// <summary>
    /// The whole lyric style: where lyrics are shown, and how they look there.
    /// </summary>
    /// <remarks>
    /// Edited directly by the settings window; a preset is a named copy of it rather
    /// than a separate source of truth.
    /// </remarks>
    /// <remarks>
    /// Named for the built-in it is a copy of, rather than left as "Custom", so a fresh
    /// install starts on a design that has a counterpart in the other display mode.
    /// </remarks>
    public LyricsAppearance LyricsStyle { get; set; } =
        new() { Name = "Widget", Schema = LyricsAppearance.CurrentSchema };

    /// <summary>What the panel does when the pointer is over it.</summary>
    /// <remarks>
    /// Not part of the style, and so not carried by a preset: it is a preference about
    /// how the panel should get out of your way, not a description of a look.
    /// </remarks>
    public LyricsHoverBehavior LyricsHover { get; set; } = LyricsHoverBehavior.Fade;

    /// <summary>
    /// Whether the panel lights each word as it is sung, or the whole line at once.
    /// </summary>
    /// <remarks>
    /// Word timing is estimated from the line for almost every track, since virtually
    /// no source carries it. The estimate is good but it is an estimate, and on a
    /// track it fits badly the whole line at once is the calmer choice. Like the hover
    /// behavior, it is a preference rather than a look, so presets leave it alone.
    /// </remarks>
    public bool LyricsWordByWord { get; set; } = true;

    /// <summary>The bar count when nothing says otherwise, and the shipped design.</summary>
    public const int DefaultBarCount = 4;

    /// <summary>
    /// Narrowest and widest bar counts on offer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling is six for two independent reasons, either of which would set it
    /// there alone. Visually, the bars divide a fixed width, so seven bars are 1.7px
    /// wide and the tall neighboring ones visibly merge at 100% scaling — worse on
    /// the light taskbar, where the bar color is translucent as well as thin.
    /// </para>
    /// <para>
    /// Technically, a 1024-point FFT at 48kHz resolves 46.9Hz per bin, and the
    /// lowest band spans 40Hz to <c>40 × 256^(1/n)</c>. At seven bands that upper
    /// edge is 89.8Hz, which falls in the same bin the second band starts from, so
    /// the lowest band becomes a strict subset of its neighbor and the two bottom
    /// bars move as one.
    /// </para>
    /// </remarks>
    public const int MinBarCount = 4;
    public const int MaxBarCount = 6;

    /// <summary>
    /// How many bars the visualizer draws. Bars share a fixed width and ink budget,
    /// so this trades detail for thickness and never widens the widget.
    /// </summary>
    public int VisualizerBarCount { get; set; } = DefaultBarCount;

    /// <summary>
    /// Forces hand-edited values back into range, and folds an older file forward.
    /// </summary>
    /// <remarks>
    /// The file is documented as safe to edit, so out-of-range values are an
    /// expected input rather than a corrupt one. Clamping beats rejecting the whole
    /// file: a mistyped bar count should not also discard the user's color.
    /// </remarks>
    public void Normalize()
    {
        MigrateLyricsStyle();

        VisualizerBarCount = Math.Clamp(VisualizerBarCount, MinBarCount, MaxBarCount);

        // A hand-edited or missing style block must not stop the widget starting.
        LyricsStyle = (LyricsStyle ?? new LyricsAppearance()).Normalize();
    }

    // ---- Version 1 -------------------------------------------------------

    /*
        Written by builds before the lyric style became one object, read once and then
        cleared. Nullable so that "absent" is distinguishable from "set to the default"
        — and so that they disappear from the file the moment the migration has run,
        rather than lingering as dead keys that look like live settings.
    */

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LyricsAppearance? PanelAppearance { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LyricsAppearance? InlineAppearance { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LyricsDisplayMode? LyricsDisplay { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LyricsPanelPosition? LyricsPosition { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LyricsCustomX { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LyricsCustomY { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LyricsPanelWidth { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LyricsPanelHeight { get; set; }

    /// <summary>
    /// Folds a version 1 file into <see cref="LyricsStyle"/>.
    /// </summary>
    /// <remarks>
    /// The two appearances become one, so the one for the mode that was not in use is
    /// dropped. That is the point of the change rather than a casualty of it: keeping
    /// both is what made a preset mean two different things depending on where the
    /// lyrics happened to be.
    /// </remarks>
    private void MigrateLyricsStyle()
    {
        if (PanelAppearance is null && InlineAppearance is null) return;

        var style = (LyricsDisplay == LyricsDisplayMode.Panel ? PanelAppearance : InlineAppearance)
            ?? new LyricsAppearance();

        style.Display = LyricsDisplay ?? LyricsDisplayMode.Inline;
        style.Position = LyricsPosition ?? style.Position;
        style.CustomX = LyricsCustomX ?? style.CustomX;
        style.CustomY = LyricsCustomY ?? style.CustomY;
        style.PanelWidth = LyricsPanelWidth ?? style.PanelWidth;
        style.PanelHeight = LyricsPanelHeight ?? style.PanelHeight;
        style.Schema = LyricsAppearance.CurrentSchema;

        LyricsStyle = style;

        PanelAppearance = null;
        InlineAppearance = null;
        LyricsDisplay = null;
        LyricsPosition = null;
        LyricsCustomX = null;
        LyricsCustomY = null;
        LyricsPanelWidth = null;
        LyricsPanelHeight = null;

        Version = CurrentVersion;
    }
}
