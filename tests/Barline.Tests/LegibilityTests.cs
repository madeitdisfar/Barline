using System.Windows.Media;
using Barline.Ui;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// The contrast guarantee. An album's dominant hue is whatever the artwork happens to
/// contain, so the widget cannot assume anything about it — these tests exist so that
/// tuning the lightness bands or saturation clamps cannot quietly reintroduce bars
/// that are technically coloured and practically invisible.
/// </summary>
public class LegibilityTests
{
    /// <summary>
    /// What a real Windows 11 taskbar measures at, as opposed to the deliberately
    /// pessimistic estimates the correction aims at. Test-only reference points.
    /// </summary>
    private static readonly Color RealDarkTaskbar = Color.FromRgb(0x1F, 0x1F, 0x1F);
    private static readonly Color RealLightTaskbar = Color.FromRgb(0xF3, 0xF3, 0xF3);

    private static readonly double[] Saturations =
        [0.06, 0.10, 0.20, 0.35, 0.50, 0.65, 0.80, 0.95, 1.00];

    private static readonly double[] Lightnesses =
        [0.02, 0.10, 0.25, 0.50, 0.75, 0.90, 0.98];

    public static TheoryData<bool> Themes => new() { true, false };

    private static Color BackdropFor(bool preferLighter) =>
        preferLighter ? Theme.DarkBackdrop : Theme.LightBackdrop;

    private static Color RealTaskbarFor(bool preferLighter) =>
        preferLighter ? RealDarkTaskbar : RealLightTaskbar;

    /// <summary>
    /// Sweeps the whole hue/saturation/lightness space and asserts that anything with
    /// a hue to keep comes back clearing the floor. This is the test the feature
    /// exists to satisfy.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_correctable_colour_clears_the_contrast_floor(bool preferLighter)
    {
        Color backdrop = BackdropFor(preferLighter);
        int corrected = 0;

        foreach (var input in Sweep())
        {
            var result = Legibility.TryCoerce(input, backdrop, preferLighter);
            var (_, inputSaturation, _) = ColorMath.ToHsl(input);

            if (result is null)
            {
                // Null is only legitimate for a colour with no hue to keep. Anything
                // visibly coloured must be correctable — otherwise covers silently
                // fall back to the theme colour, which is a quality regression the
                // contrast assertion below cannot see, because it never runs.
                Assert.True(inputSaturation < 0.15d,
                    $"{input} has saturation {inputSaturation:F2} but was uncorrectable");
                continue;
            }

            corrected++;
            double ratio = ColorMath.ContrastRatio(result.Value, backdrop);

            Assert.True(ratio >= Legibility.MinContrast - 1e-9,
                $"{input} -> {result.Value} only reached {ratio:F2}:1 against {backdrop}");
        }

        // Guards against a vacuous pass: if a change made TryCoerce return null for
        // everything, the loop above would assert nothing at all.
        Assert.True(corrected > 3000, $"expected most inputs to be correctable, got {corrected}");
    }

    /// <summary>
    /// The estimates are pessimistic on purpose, so a real taskbar must come out with
    /// more contrast than asked for, never less.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Correction_leaves_headroom_against_a_real_taskbar(bool preferLighter)
    {
        Color backdrop = BackdropFor(preferLighter);
        Color real = RealTaskbarFor(preferLighter);

        foreach (var input in Sweep())
        {
            var result = Legibility.TryCoerce(input, backdrop, preferLighter);
            if (result is null) continue;

            double againstEstimate = ColorMath.ContrastRatio(result.Value, backdrop);
            double againstReal = ColorMath.ContrastRatio(result.Value, real);

            Assert.True(againstReal >= againstEstimate,
                $"{result.Value}: real taskbar {againstReal:F2}:1 is worse than " +
                $"the estimate's {againstEstimate:F2}:1");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Correction_preserves_hue(bool preferLighter)
    {
        Color backdrop = BackdropFor(preferLighter);

        foreach (var input in Sweep())
        {
            var result = Legibility.TryCoerce(input, backdrop, preferLighter);
            if (result is null) continue;

            // Measured against the input colour's own hue, not an ideal one: at low
            // saturation an 8-bit colour cannot represent an exact hue, and that error
            // is not the correction's doing.
            var (inHue, _, _) = ColorMath.ToHsl(input);
            var (outHue, _, _) = ColorMath.ToHsl(result.Value);
            double drift = Math.Abs(((outHue - inHue + 540d) % 360d) - 180d);

            Assert.True(drift < 5d, $"{input} -> {result.Value} moved hue by {drift:F1} degrees");
        }
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00)]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0x80, 0x80, 0x80)]
    [InlineData(0x33, 0x33, 0x33)]
    [InlineData(0x10, 0x10, 0x10)]
    public void Greys_are_rejected_rather_than_tinted(byte r, byte g, byte b)
    {
        var grey = Color.FromRgb(r, g, b);

        // There is no hue to preserve, so any colour returned here would be one the
        // saturation floor invented — a tint that is not in the source.
        Assert.Null(Legibility.TryCoerce(grey, Theme.DarkBackdrop, preferLighter: true));
        Assert.Null(Legibility.TryCoerce(grey, Theme.LightBackdrop, preferLighter: false));
    }

    /// <summary>
    /// Regression test for a bug a contrast floor alone does not catch: a pale cover
    /// arrives at lightness 0.98, already clears the floor against the dark taskbar
    /// untouched, and paints bars indistinguishable from white — the album's colour
    /// technically preserved and visually gone. The lightness band is bounded above
    /// for exactly this case.
    /// </summary>
    [Fact]
    public void A_pale_source_is_pulled_back_to_a_visible_colour()
    {
        var pale = ColorMath.FromHsl(340d, 1.0d, 0.98d);

        var result = Legibility.TryCoerce(pale, Theme.DarkBackdrop, preferLighter: true);

        Assert.NotNull(result);
        var (_, saturation, lightness) = ColorMath.ToHsl(result.Value);

        Assert.True(lightness <= 0.82d, $"stayed near-white at lightness {lightness:F2}");
        Assert.True(saturation >= 0.35d, $"washed out to saturation {saturation:F2}");
    }

    /// <summary>
    /// The mirror case, and a standing request from testing: the light taskbar must
    /// never get near-black bars, which read as far too heavy next to the text.
    /// </summary>
    [Fact]
    public void A_dark_source_is_lifted_off_black_on_the_light_taskbar()
    {
        var nearBlack = ColorMath.FromHsl(220d, 1.0d, 0.03d);

        var result = Legibility.TryCoerce(nearBlack, Theme.LightBackdrop, preferLighter: false);

        Assert.NotNull(result);
        var (_, saturation, lightness) = ColorMath.ToHsl(result.Value);

        Assert.True(lightness >= 0.20d, $"stayed near-black at lightness {lightness:F2}");
        Assert.True(saturation >= 0.35d, $"washed out to saturation {saturation:F2}");
    }

    [Fact]
    public void A_saturated_blue_is_the_hardest_case_and_still_passes()
    {
        // Blue carries only 7% of luminance, so it needs the most correction to be
        // seen on a dark backdrop. Worth naming explicitly rather than trusting the
        // sweep to cover it.
        var deepBlue = ColorMath.FromHsl(240d, 1.0d, 0.35d);

        var result = Legibility.TryCoerce(deepBlue, Theme.DarkBackdrop, preferLighter: true);

        Assert.NotNull(result);
        Assert.True(
            ColorMath.ContrastRatio(result.Value, Theme.DarkBackdrop) >= Legibility.MinContrast,
            $"deep blue only reached {ColorMath.ContrastRatio(result.Value, Theme.DarkBackdrop):F2}:1");
    }

    private static IEnumerable<Color> Sweep()
    {
        for (int hue = 0; hue < 360; hue += 5)
            foreach (double saturation in Saturations)
                foreach (double lightness in Lightnesses)
                    yield return ColorMath.FromHsl(hue, saturation, lightness);
    }
}
