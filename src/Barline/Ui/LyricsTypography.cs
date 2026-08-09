using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Barline.Lyrics;

namespace Barline.Ui;

/// <summary>
/// Turns a <see cref="LyricsAppearance"/> into the WPF properties that realize it.
/// </summary>
/// <remarks>
/// Shared so the floating panel and the inline display read the same appearance the
/// same way. They differ only in which parts apply — the inline display has no surface
/// of its own — and keeping the translation in one place is what stops the two drifting
/// into subtly different interpretations of the same preset.
/// </remarks>
internal static class LyricsTypography
{
    public static Color TextColor(LyricsAppearance appearance) =>
        Parse(appearance.TextColor, Colors.White);

    /// <summary>The text color at the opacity an unsung word is drawn with.</summary>
    public static Color UnsungColor(LyricsAppearance appearance)
    {
        var color = TextColor(appearance);

        return Color.FromArgb(
            (byte)Math.Round(255d * Math.Clamp(appearance.UnsungOpacity, 0d, 1d)),
            color.R,
            color.G,
            color.B);
    }

    public static Color EffectColor(LyricsAppearance appearance) =>
        string.IsNullOrWhiteSpace(appearance.EffectColor)
            ? TextColor(appearance)
            : Parse(appearance.EffectColor, TextColor(appearance));

    /// <summary>
    /// Applies type to a line. The fallback chain keeps a preset naming a font the
    /// machine does not have from rendering in whatever WPF picks by default.
    /// </summary>
    public static void ApplyFont(TextBlock line, LyricsAppearance appearance)
    {
        line.FontFamily = new FontFamily(
            $"{appearance.FontFamily}, Segoe UI Variable Display, Segoe UI");

        line.FontSize = appearance.FontSize;
        line.FontWeight = Weight(appearance.FontWeight);
        line.FontStyle = appearance.Italic ? FontStyles.Italic : FontStyles.Normal;

        // Line height tracks the type, or a larger size would clip against a height
        // set for a smaller one.
        line.LineHeight = Math.Round(appearance.FontSize * 1.3d);
    }

    public static FontWeight Weight(string name) => name?.ToLowerInvariant() switch
    {
        "normal" or "regular" => FontWeights.Normal,
        "bold" => FontWeights.Bold,
        _ => FontWeights.SemiBold,
    };

    /// <summary>The effect to hang behind the text, or null for none.</summary>
    /// <remarks>
    /// Always applied to a static copy of the line rather than to the live text: the
    /// word highlight changes every frame, and an effect on it would be re-rendered
    /// every frame with it.
    /// </remarks>
    public static Effect? BuildEffect(LyricsAppearance appearance) => appearance.Effect switch
    {
        LyricsEffect.Glow => new BlurEffect
        {
            Radius = Math.Max(1d, appearance.EffectRadius),
            KernelType = KernelType.Gaussian,
        },
        _ => null,
    };

    public static string Present(string text, LyricsAppearance appearance) =>
        appearance.Lowercase ? text.ToLowerInvariant() : text;

    /// <summary>Reads <c>#RRGGBB</c> or <c>#AARRGGBB</c>, falling back rather than throwing.</summary>
    public static Color Parse(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            var parsed = ColorConverter.ConvertFromString(value.Trim());
            return parsed is Color color ? color : fallback;
        }
        catch
        {
            // A hand-edited preset with a typo should look wrong, not crash.
            return fallback;
        }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
