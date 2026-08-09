using System.Windows.Media;

namespace Barline.Ui;

/// <summary>
/// Forces an arbitrary color to be visible against the taskbar, keeping its hue.
/// </summary>
/// <remarks>
/// <para>
/// This is the guarantee behind the album-art and accent color modes. A hue taken
/// from artwork is whatever the artwork happened to contain: a dark navy cover on the
/// dark taskbar, or a pale yellow one on the light taskbar, would produce bars that
/// are technically colored and practically invisible. Hue and (within a band)
/// saturation are the artwork's to choose; lightness is not.
/// </para>
/// <para>
/// A pure function, separate from <see cref="BarColorResolver"/>, so the contrast
/// floor can be swept across the whole hue/saturation space and checked directly
/// rather than eyeballed on one album cover.
/// </para>
/// </remarks>
internal static class Legibility
{
    /// <summary>
    /// Minimum contrast against the taskbar. WCAG SC 1.4.11's threshold for non-text
    /// graphics — the bars are 3px wide, so this is a floor, not a target.
    /// </summary>
    public const double MinContrast = 3.0d;

    // The hue has to survive being pushed toward light or dark, so saturation is held
    // inside a band: too low and the correction bleaches it to gray, too high and an
    // album tint turns into neon that stops reading as system UI.
    private const double MinSaturation = 0.40d;
    private const double MaxSaturation = 0.85d;

    /// <summary>Below this there is no hue to preserve and correcting is meaningless.</summary>
    private const double GrayThreshold = 0.05d;

    // The lightness band a corrected color starts inside, chosen around the
    // hand-tuned Default colors so a corrected bar carries the same visual weight as
    // the built-in one.
    //
    // Bounded on BOTH sides on purpose. A floor alone is not enough: a pale pink cover
    // arrives at lightness 0.98, clears the contrast floor against the dark taskbar
    // untouched, and paints bars that are legible but indistinguishable from white —
    // the album's color technically preserved and visually gone. The ceiling pulls it
    // back to where the hue is actually visible. On the light taskbar the same bound
    // stops a dark cover from painting the near-black bars that read as too heavy.
    private const double DarkThemeMinLightness = 0.62d;
    private const double DarkThemeMaxLightness = 0.80d;
    private const double LightThemeMinLightness = 0.28d;
    private const double LightThemeMaxLightness = 0.42d;

    private const double LightnessStep = 0.02d;
    private const int MaxSteps = 60;

    /// <summary>
    /// Returns <paramref name="color"/>'s hue at a lightness that clears
    /// <see cref="MinContrast"/> against <paramref name="backdrop"/>, or null when the
    /// input has no hue to keep or no lightness would satisfy the floor.
    /// </summary>
    /// <param name="preferLighter">
    /// True to correct upward, for a dark taskbar. Correcting the other way would have
    /// to cross the backdrop's own luminance before it could gain contrast.
    /// </param>
    public static Color? TryCoerce(Color color, Color backdrop, bool preferLighter)
    {
        var (hue, saturation, lightness) = ColorMath.ToHsl(color);

        if (saturation < GrayThreshold) return null;

        saturation = Math.Clamp(saturation, MinSaturation, MaxSaturation);

        // Start inside the band, keeping the input's relative lightness within it, so
        // a pale cover still reads slightly lighter than a dark one.
        lightness = preferLighter
            ? Math.Clamp(lightness, DarkThemeMinLightness, DarkThemeMaxLightness)
            : Math.Clamp(lightness, LightThemeMinLightness, LightThemeMaxLightness);

        // Stepping rather than solving: HSL lightness maps to relative luminance
        // non-linearly and differently per hue, so walking until the ratio passes is
        // both simpler and exact. It runs once per track change.
        for (int step = 0; step < MaxSteps; step++)
        {
            var candidate = ColorMath.FromHsl(hue, saturation, lightness);

            if (ColorMath.ContrastRatio(candidate, backdrop) >= MinContrast)
                return candidate;

            lightness += preferLighter ? LightnessStep : -LightnessStep;
            if (lightness is > 1d or < 0d) break;
        }

        return null;
    }
}
