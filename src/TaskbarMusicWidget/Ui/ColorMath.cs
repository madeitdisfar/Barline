using System.Windows.Media;

namespace TaskbarMusicWidget.Ui;

/// <summary>
/// Colour-space helpers shared by album-art extraction and legibility correction.
/// </summary>
/// <remarks>
/// <para>
/// HSL rather than HSV because the operation that matters here is "keep the hue the
/// cover gave us, move only how light it is until it can be seen" — one axis in HSL.
/// </para>
/// <para>
/// Contrast is WCAG 2.x relative luminance. The bars are a graphical element, not
/// text, so the relevant bar is SC 1.4.11's 3:1 rather than 4.5:1.
/// </para>
/// <para>
/// Alpha is ignored throughout: every colour that goes through contrast correction is
/// opaque, and blending a translucent bar against estimated taskbar material would be
/// guesswork stacked on guesswork.
/// </para>
/// </remarks>
internal static class ColorMath
{
    public static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2d;
        double delta = max - min;

        // Pure grey: hue is undefined, and any value we invented would later be
        // amplified by the saturation floor into a tint that is not in the artwork.
        if (delta <= double.Epsilon) return (0d, 0d, lightness);

        double saturation = lightness > 0.5d
            ? delta / (2d - max - min)
            : delta / (max + min);

        double hue;
        if (max == r) hue = ((g - b) / delta + (g < b ? 6d : 0d)) * 60d;
        else if (max == g) hue = ((b - r) / delta + 2d) * 60d;
        else hue = ((r - g) / delta + 4d) * 60d;

        return (hue, saturation, lightness);
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = ((hue % 360d) + 360d) % 360d;
        saturation = Math.Clamp(saturation, 0d, 1d);
        lightness = Math.Clamp(lightness, 0d, 1d);

        double chroma = (1d - Math.Abs(2d * lightness - 1d)) * saturation;
        double sector = hue / 60d;
        double second = chroma * (1d - Math.Abs(sector % 2d - 1d));

        // Normalising hue above keeps sector < 6, so the final arm is sector 5.
        (double r, double g, double b) = (int)sector switch
        {
            0 => (chroma, second, 0d),
            1 => (second, chroma, 0d),
            2 => (0d, chroma, second),
            3 => (0d, second, chroma),
            4 => (second, 0d, chroma),
            _ => (chroma, 0d, second),
        };

        double match = lightness - chroma / 2d;
        return Color.FromRgb(Channel(r + match), Channel(g + match), Channel(b + match));
    }

    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255d), 0d, 255d);

    public static double RelativeLuminance(Color color) =>
        0.2126d * Linearise(color.R) +
        0.7152d * Linearise(color.G) +
        0.0722d * Linearise(color.B);

    /// <summary>Undoes the sRGB transfer curve, per WCAG's definition.</summary>
    private static double Linearise(byte channel)
    {
        double v = channel / 255d;
        return v <= 0.03928d ? v / 12.92d : Math.Pow((v + 0.055d) / 1.055d, 2.4d);
    }

    /// <summary>Contrast ratio between two colours, from 1:1 to 21:1.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return la > lb
            ? (la + 0.05d) / (lb + 0.05d)
            : (lb + 0.05d) / (la + 0.05d);
    }
}
