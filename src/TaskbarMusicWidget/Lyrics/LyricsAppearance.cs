namespace TaskbarMusicWidget.Lyrics;

/// <summary>
/// What sits behind the lyrics in the floating panel.
/// </summary>
/// <remarks>
/// A compositor-blurred acrylic option used to live here and was removed. Windows
/// composites that blur across the whole window rectangle, and a transparent window
/// takes its shape from per-pixel alpha rather than from a region, so acrylic could
/// never honour a corner radius — one background behaving differently from the rest
/// was not worth what it bought.
/// </remarks>
internal enum LyricsBackground
{
    /// <summary>A flat colour at the chosen opacity: see-through, but not blurred.</summary>
    Tinted,

    /// <summary>A flat opaque colour.</summary>
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
/// colour — and those are settings. Two names for one effect is a worse choice to
/// offer than one.
/// </remarks>
internal enum LyricsEffect
{
    None,

    /// <summary>A blurred copy of the line beneath it — a halo at a wide radius, a soft edge at a narrow one.</summary>
    Glow,
}

/// <summary>
/// Everything about how lyrics look, independent of where they are shown.
/// </summary>
/// <remarks>
/// <para>
/// Serialised both into the settings file — as the live values the settings window
/// edits — and on its own as a preset. One type covers both, so a preset is exactly a
/// saved copy of what you are looking at, with no second schema to keep in step.
/// </para>
/// <para>
/// The inline display ignores everything under Background and Corner: it is drawn on
/// the widget, which deliberately paints no background of its own so the taskbar's
/// material shows through. Giving it one would break the thing the widget is built
/// around.
/// </para>
/// </remarks>
internal sealed class LyricsAppearance
{
    /// <summary>Shown in the settings window; set when loaded from a preset.</summary>
    public string Name { get; set; } = "Custom";

    // ---- Type -------------------------------------------------------------

    public string FontFamily { get; set; } = "Segoe UI Variable Display";

    public double FontSize { get; set; } = 20d;

    /// <summary>One of Normal, SemiBold or Bold.</summary>
    public string FontWeight { get; set; } = "SemiBold";

    /// <summary>Lowercases the text, which some looks depend on.</summary>
    public bool Lowercase { get; set; }

    public bool Italic { get; set; }

    // ---- Colour -----------------------------------------------------------

    /// <summary>Colour of a word once it has been sung, as <c>#RRGGBB</c>.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>
    /// How visible a word is before it is sung, 0..1. Also the opacity of the whole
    /// line when highlighting a line at a time.
    /// </summary>
    public double UnsungOpacity { get; set; } = 0.38d;

    // ---- Effect -----------------------------------------------------------

    public LyricsEffect Effect { get; set; } = LyricsEffect.None;

    public double EffectRadius { get; set; } = 16d;

    /// <summary>Effect colour; empty means take the text colour.</summary>
    public string EffectColor { get; set; } = string.Empty;

    // ---- Panel surface ----------------------------------------------------

    public LyricsBackground Background { get; set; } = LyricsBackground.Tinted;

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

        if (string.IsNullOrWhiteSpace(FontFamily)) FontFamily = "Segoe UI Variable Display";
        if (string.IsNullOrWhiteSpace(Name)) Name = "Custom";

        return this;
    }

    public const double MinFontSize = 9d;
    public const double MaxFontSize = 48d;
    public const double MaxCornerRadius = 32d;

    public LyricsAppearance Clone() => (LyricsAppearance)MemberwiseClone();

    // ---- The looks that ship ----------------------------------------------

    /// <summary>
    /// The three built-in looks. Written to disk as ordinary preset files on first
    /// run, so they can be read, copied and edited like any other.
    /// </summary>
    public static IReadOnlyList<LyricsAppearance> BuiltIn { get; } =
    [
        new LyricsAppearance
        {
            Name = "Clean",
        },
        new LyricsAppearance
        {
            Name = "Glow",
            Effect = LyricsEffect.Glow,
            EffectRadius = 16d,
        },
        new LyricsAppearance
        {
            Name = "Lime",
            FontFamily = "Arial Narrow",
            FontWeight = "Bold",
            Lowercase = true,
            TextColor = "#101208",
            UnsungOpacity = 0.45d,
            Effect = LyricsEffect.Glow,
            EffectRadius = 4d,
            Background = LyricsBackground.Solid,
            BackgroundColor = "#8ACE00",
            BackgroundOpacity = 1d,
            CornerRadius = 8d,
        },
    ];

    /// <summary>The look the inline display starts with — plain, and no surface.</summary>
    public static LyricsAppearance DefaultInline() => new()
    {
        Name = "Clean",
        FontFamily = "Segoe UI Variable Text",
        FontSize = 12d,
        FontWeight = "SemiBold",
        UnsungOpacity = 0.45d,
        Background = LyricsBackground.None,
    };
}
