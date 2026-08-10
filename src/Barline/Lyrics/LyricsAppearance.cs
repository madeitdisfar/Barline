using System.Text.Json.Serialization;

namespace Barline.Lyrics;

/// <summary>
/// What sits behind the lyrics in the floating panel.
/// </summary>
/// <remarks>
/// A compositor-blurred acrylic option used to live here and was removed. Windows
/// composites that blur across the whole window rectangle, and a transparent window
/// takes its shape from per-pixel alpha rather than from a region, so acrylic could
/// never honor a corner radius — one background behaving differently from the rest
/// was not worth what it bought.
/// </remarks>
internal enum LyricsBackground
{
    /// <summary>A flat color at the chosen opacity: see-through, but not blurred.</summary>
    Tinted,

    /// <summary>A flat opaque color.</summary>
    Solid,

    /// <summary>Nothing at all — text straight over the desktop.</summary>
    None,
}

/// <summary>
/// An effect drawn behind the text.
/// </summary>
/// <remarks>
/// A "Soften" option used to sit alongside Glow and was removed: both drew a blurred
/// copy of the line underneath, so the only thing separating them was the radius and
/// color — and those are settings. Two names for one effect is a worse choice to
/// offer than one.
/// </remarks>
internal enum LyricsEffect
{
    None,

    /// <summary>A blurred copy of the line beneath it — a halo at a wide radius, a soft edge at a narrow one.</summary>
    Glow,
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
    /// A panel floating just above the taskbar, with room for a full line. Reads far
    /// better; costs a second window.
    /// </summary>
    Panel,
}

/// <summary>Where the lyrics panel sits on screen.</summary>
internal enum LyricsPanelPosition
{
    /// <summary>Just above the widget, tracking the taskbar's left edge.</summary>
    AboveWidget,

    /// <summary>Centered horizontally, just above the taskbar.</summary>
    BottomCenter,

    /// <summary>Centered horizontally, near the top of the screen.</summary>
    TopCenter,

    /// <summary>
    /// Wherever <see cref="LyricsAppearance.CustomX"/> and
    /// <see cref="LyricsAppearance.CustomY"/> put it.
    /// </summary>
    Custom,
}

/// <summary>
/// A complete lyric style: where the lyrics are shown, and how they look there.
/// </summary>
/// <remarks>
/// <para>
/// Placement lives here rather than beside it in the settings because the two are not
/// really separable. A 20px line over a tinted panel and a 12px line in the widget are
/// different looks, and a look that does not say which of the two it is describes
/// nothing — the same preset applied to both modes was the confusion this replaced.
/// So a preset carries the mode, the anchor and the panel size along with the type and
/// color, and loading one puts the lyrics where that look was designed to live.
/// </para>
/// <para>
/// What is deliberately <em>not</em> here: whether lyrics are on at all, whether they
/// light up a word or a line at a time, and what the panel does when hovered. Those are
/// preferences about behavior rather than descriptions of a look, and carrying them in
/// a shared preset would mean someone else's file quietly changing how yours behaves.
/// </para>
/// <para>
/// Serialized both into the settings file — as the live values the settings window
/// edits — and on its own as a preset. One type covers both, so a preset is exactly a
/// saved copy of what you are looking at, with no second schema to keep in step.
/// </para>
/// <para>
/// The inline display ignores everything under Panel: the widget deliberately paints no
/// background of its own so the taskbar's material shows through, and it has nowhere to
/// be positioned or resized to.
/// </para>
/// </remarks>
internal sealed class LyricsAppearance
{
    /// <summary>Shown in the settings window; set when loaded from a preset.</summary>
    public string Name { get; set; } = "Custom";

    /// <summary>
    /// What wrote this. Version 2 folded placement in, so a version 1 file describes
    /// only how lyrics look and says nothing about where they go.
    /// </summary>
    /// <remarks>
    /// Deliberately without an initializer. A property that defaults to the current
    /// version reads back as current from a file that never mentioned it, which is
    /// exactly the case this exists to detect — the serializer leaves absent keys at
    /// whatever the object was constructed with. Stamped on the way out by
    /// <see cref="LyricsPresetStore.Write"/>, so anything we wrote says so and anything
    /// older reads as zero.
    /// </remarks>
    public int Schema { get; set; }

    public const int CurrentSchema = 2;

    // ---- Placement --------------------------------------------------------

    /// <summary>Where the line is drawn. Everything below under Panel applies only to <see cref="LyricsDisplayMode.Panel"/>.</summary>
    public LyricsDisplayMode Display { get; set; } = LyricsDisplayMode.Inline;

    public LyricsPanelPosition Position { get; set; } = LyricsPanelPosition.AboveWidget;

    /// <summary>
    /// Free position, as a percentage across and down the monitor's usable area.
    /// </summary>
    /// <remarks>
    /// A proportion rather than pixels, so a panel placed by hand stays where it was put
    /// when the resolution changes or the widget moves to another screen. Zero is flush
    /// with the top-left, one hundred flush with the bottom-right — the panel's own size
    /// is taken off, so it never lands part-way off the edge.
    /// </remarks>
    public double CustomX { get; set; } = 50d;
    public double CustomY { get; set; } = 70d;

    /// <summary>Panel size in logical pixels.</summary>
    public int PanelWidth { get; set; } = DefaultPanelWidth;
    public int PanelHeight { get; set; } = DefaultPanelHeight;

    // ---- Type -------------------------------------------------------------

    public string FontFamily { get; set; } = "Segoe UI Variable Text";

    public double FontSize { get; set; } = 12d;

    /// <summary>One of Normal, SemiBold or Bold.</summary>
    public string FontWeight { get; set; } = "SemiBold";

    /// <summary>Lowercases the text, which some looks depend on.</summary>
    public bool Lowercase { get; set; }

    public bool Italic { get; set; }

    // ---- Color -----------------------------------------------------------

    /// <summary>Color of a word once it has been sung, as <c>#RRGGBB</c>.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>
    /// How visible a word is before it is sung, 0..1. Also the opacity of the whole
    /// line when highlighting a line at a time.
    /// </summary>
    public double UnsungOpacity { get; set; } = 0.45d;

    // ---- Effect -----------------------------------------------------------

    public LyricsEffect Effect { get; set; } = LyricsEffect.None;

    public double EffectRadius { get; set; } = 16d;

    /// <summary>Effect color; empty means take the text color.</summary>
    public string EffectColor { get; set; } = string.Empty;

    // ---- Panel surface ----------------------------------------------------

    public LyricsBackground Background { get; set; } = LyricsBackground.None;

    public string BackgroundColor { get; set; } = "#2C2C2C";

    /// <summary>Opacity of the background, 0..1. Ignored by <see cref="LyricsBackground.None"/>.</summary>
    public double BackgroundOpacity { get; set; } = 0.72d;

    public double CornerRadius { get; set; } = 8d;

    /// <summary>Forces hand-edited values into range.</summary>
    public LyricsAppearance Normalize()
    {
        FontSize = Math.Clamp(FontSize, MinFontSize, MaxFontSize);
        UnsungOpacity = Math.Clamp(UnsungOpacity, 0d, 1d);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0d, 1d);
        EffectRadius = Math.Clamp(EffectRadius, 0d, 40d);
        CornerRadius = Math.Clamp(CornerRadius, 0d, MaxCornerRadius);

        CustomX = Math.Clamp(CustomX, 0d, 100d);
        CustomY = Math.Clamp(CustomY, 0d, 100d);
        PanelWidth = Math.Clamp(PanelWidth, MinPanelWidth, MaxPanelWidth);
        PanelHeight = Math.Clamp(PanelHeight, MinPanelHeight, MaxPanelHeight);

        if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Segoe UI Variable Text";
        if (string.IsNullOrWhiteSpace(Name)) Name = "Custom";

        return this;
    }

    public const double MinFontSize = 9d;
    public const double MaxFontSize = 48d;
    public const double MaxCornerRadius = 32d;

    public const int DefaultPanelWidth = 520;
    public const int DefaultPanelHeight = 96;

    /// <summary>
    /// Bounds for the panel size.
    /// </summary>
    /// <remarks>
    /// The floor is low enough to suit the smallest font on offer — a 9px line needs
    /// very little room — and the ceiling keeps the panel from covering so much of the
    /// screen that it stops reading as an overlay.
    /// </remarks>
    public const int MinPanelWidth = 180;
    public const int MaxPanelWidth = 1400;
    public const int MinPanelHeight = 36;
    public const int MaxPanelHeight = 400;

    public LyricsAppearance Clone() => (LyricsAppearance)MemberwiseClone();

    /// <summary>
    /// Takes the placement from another style, leaving the look alone.
    /// </summary>
    /// <remarks>
    /// For a preset written before placement was part of one: it says nothing about
    /// where the lyrics go, so loading it must leave them where they are rather than
    /// assert a default it never chose.
    /// </remarks>
    public void TakePlacementFrom(LyricsAppearance other)
    {
        Display = other.Display;
        Position = other.Position;
        CustomX = other.CustomX;
        CustomY = other.CustomY;
        PanelWidth = other.PanelWidth;
        PanelHeight = other.PanelHeight;
    }

    // ---- The looks that ship ----------------------------------------------

    /// <summary>
    /// Whether this look uses anything the free build cannot draw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decided by what the style contains rather than by what it is called, which is the
    /// only workable test: built-ins and saved presets are the same kind of file in the
    /// same folder, and a file written while licensed outlives the license. Asking the
    /// content means a preset is judged the same way whoever wrote it and whenever.
    /// </para>
    /// <para>
    /// Only two of the paid features can appear in a style at all. Bar count and bar
    /// color live in <see cref="Settings.WidgetSettings"/>, and saving or importing is
    /// an action rather than a value, so neither can be carried by a preset.
    /// </para>
    /// </remarks>
    [JsonIgnore]
    public bool UsesPremium =>
        Effect != LyricsEffect.None || Position == LyricsPanelPosition.Custom;

    // ---- The looks that ship ----------------------------------------------

    /// <summary>
    /// The built-in looks. Written to disk as ordinary preset files on first run, so
    /// they can be read, copied and edited like any other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three for the widget and four for the panel, so neither display mode is a
    /// one-way trip now that a preset carries where it belongs.
    /// </para>
    /// <para>
    /// Four of them glow, which makes them paid, and the free build never writes those
    /// files at all. What is left is Widget, Widget_Movie and Clean: one plain line, one
    /// styled line, and one panel. That is the floor the free build has to look finished
    /// at, and it is the reason the glow is what got gated rather than the background or
    /// the color, either of which would have taken the remaining three down with it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<LyricsAppearance> BuiltIn { get; } =
    [
        // Exactly the defaults above: a plain line in the widget, no surface, because
        // the taskbar's own material is what should show through.
        new LyricsAppearance { Name = "Widget" },

        Inline("Widget_Glow", p =>
        {
            p.Effect = LyricsEffect.Glow;
            p.EffectRadius = 16d;
        }),

        Inline("Widget_Movie", p =>
        {
            p.Lowercase = true;
            p.Italic = true;
            p.TextColor = "#B8B81E";
        }),

        Panel("Clean", p => p.FontSize = 18d),

        Panel("Glow", p =>
        {
            p.FontSize = 18d;
            p.Effect = LyricsEffect.Glow;
            p.EffectRadius = 18d;
        }),

        Panel("Movie", p =>
        {
            p.FontSize = 16d;
            p.Italic = true;
            p.TextColor = "#B8B81E";
            p.Effect = LyricsEffect.Glow;
            p.EffectRadius = 16d;
        }),

        Panel("Raw", p =>
        {
            p.FontFamily = "Arial Narrow";
            p.FontSize = 20d;
            p.FontWeight = "Bold";
            p.Lowercase = true;
            p.TextColor = "#101208";
            p.UnsungOpacity = 0.45d;
            p.Effect = LyricsEffect.Glow;
            p.EffectRadius = 4d;
            p.Background = LyricsBackground.Solid;
            p.BackgroundColor = "#D9D9D9";
            p.BackgroundOpacity = 1d;
        }),
    ];

    /// <summary>
    /// Built-ins that shipped once and no longer do.
    /// </summary>
    /// <remarks>
    /// Kept in full rather than as bare names because withdrawing one means deleting a
    /// file out of the user's folder, and the only safe warrant for that is that the
    /// file is still ours. Lime was replaced by Raw, which is the same design on a
    /// neutral surface.
    /// </remarks>
    public static IReadOnlyList<LyricsAppearance> Retired { get; } =
    [
        new LyricsAppearance
        {
            Name = "Lime",
            Display = LyricsDisplayMode.Panel,
            FontFamily = "Arial Narrow",
            FontSize = 20d,
            FontWeight = "Bold",
            Lowercase = true,
            TextColor = "#101208",
            UnsungOpacity = 0.45d,
            Effect = LyricsEffect.Glow,
            EffectRadius = 4d,
            Background = LyricsBackground.Solid,
            BackgroundColor = "#8ACE00",
            BackgroundOpacity = 1d,
        },
    ];

    /// <summary>
    /// Whether two styles describe the same look, ignoring the name and the schema.
    /// </summary>
    /// <remarks>
    /// The test for "still ours". A retired built-in is removed only when it matches
    /// what we wrote, so a user who edited one and kept the name keeps their work.
    /// </remarks>
    public bool LooksLike(LyricsAppearance other) =>
        Display == other.Display
        && Position == other.Position
        && CustomX.Equals(other.CustomX)
        && CustomY.Equals(other.CustomY)
        && PanelWidth == other.PanelWidth
        && PanelHeight == other.PanelHeight
        && FontFamily == other.FontFamily
        && FontSize.Equals(other.FontSize)
        && FontWeight == other.FontWeight
        && Lowercase == other.Lowercase
        && Italic == other.Italic
        && TextColor == other.TextColor
        && UnsungOpacity.Equals(other.UnsungOpacity)
        && Effect == other.Effect
        && EffectRadius.Equals(other.EffectRadius)
        && EffectColor == other.EffectColor
        && Background == other.Background
        && BackgroundColor == other.BackgroundColor
        && BackgroundOpacity.Equals(other.BackgroundOpacity)
        && CornerRadius.Equals(other.CornerRadius);

    /// <summary>The panel's shared starting point: larger type, and a surface to sit on.</summary>
    private static LyricsAppearance Panel(string name, Action<LyricsAppearance>? adjust = null) =>
        Shared(name, LyricsDisplayMode.Panel, "Segoe UI Variable Display", 20d,
            LyricsBackground.Tinted, adjust);

    /// <summary>
    /// A widget line that is not the plain one.
    /// </summary>
    /// <remarks>
    /// Never carries a background, whatever the file it came from said. The widget
    /// paints none by design, so the value is inert there and only becomes visible if
    /// the look is later switched to the panel — where it would apply a surface nobody
    /// asked for. The panel size is kept, because that is placement rather than paint
    /// and is what the panel should open at.
    /// </remarks>
    private static LyricsAppearance Inline(string name, Action<LyricsAppearance>? adjust = null) =>
        Shared(name, LyricsDisplayMode.Inline, "Segoe UI Variable Text", 12d,
            LyricsBackground.None, adjust);

    private static LyricsAppearance Shared(
        string name,
        LyricsDisplayMode display,
        string family,
        double size,
        LyricsBackground background,
        Action<LyricsAppearance>? adjust)
    {
        var preset = new LyricsAppearance
        {
            Name = name,
            Display = display,
            FontFamily = family,
            FontSize = size,
            PanelWidth = 260,
            PanelHeight = 100,
            UnsungOpacity = 0.38d,
            Background = background,
        };

        adjust?.Invoke(preset);

        return preset;
    }
}
