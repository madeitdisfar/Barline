using System.Windows.Media;
using Barline.Ui;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// Pins the colour-space conversions the legibility correction is built on. If these
/// drift, every contrast guarantee above them is measuring the wrong thing.
/// </summary>
public class ColorMathTests
{
    [Theory]
    [InlineData(0xFF, 0x00, 0x00, 0d)]      // red
    [InlineData(0x00, 0xFF, 0x00, 120d)]    // green
    [InlineData(0x00, 0x00, 0xFF, 240d)]    // blue
    [InlineData(0xFF, 0xFF, 0x00, 60d)]     // yellow
    [InlineData(0x00, 0xFF, 0xFF, 180d)]    // cyan
    [InlineData(0xFF, 0x00, 0xFF, 300d)]    // magenta
    public void ToHsl_finds_the_primary_hues(byte r, byte g, byte b, double expectedHue)
    {
        var (hue, saturation, lightness) = ColorMath.ToHsl(Color.FromRgb(r, g, b));

        Assert.Equal(expectedHue, hue, precision: 3);
        Assert.Equal(1d, saturation, precision: 3);
        Assert.Equal(0.5d, lightness, precision: 3);
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0d)]
    [InlineData(0xFF, 0xFF, 0xFF, 1d)]
    [InlineData(0x80, 0x80, 0x80, 0.5019d)]
    public void ToHsl_reports_greys_as_unsaturated(byte r, byte g, byte b, double expectedLightness)
    {
        var (_, saturation, lightness) = ColorMath.ToHsl(Color.FromRgb(r, g, b));

        // Hue is deliberately not asserted: it is undefined for a grey, and the
        // callers all gate on saturation before reading it.
        Assert.Equal(0d, saturation);
        Assert.Equal(expectedLightness, lightness, precision: 3);
    }

    [Fact]
    public void FromHsl_round_trips_through_ToHsl()
    {
        // Saturation stays clear of zero and lightness clear of the extremes, where
        // 8-bit quantisation legitimately destroys hue information.
        for (int hue = 0; hue < 360; hue += 7)
        {
            foreach (double saturation in new[] { 0.35d, 0.6d, 0.85d, 1.0d })
            {
                foreach (double lightness in new[] { 0.3d, 0.5d, 0.7d })
                {
                    var color = ColorMath.FromHsl(hue, saturation, lightness);
                    var (h2, s2, l2) = ColorMath.ToHsl(color);

                    Assert.True(Math.Abs(((h2 - hue + 540d) % 360d) - 180d) < 1.0d,
                        $"hue {hue} -> {h2} via {color}");
                    Assert.True(Math.Abs(s2 - saturation) < 0.02d,
                        $"saturation {saturation} -> {s2} via {color}");
                    Assert.True(Math.Abs(l2 - lightness) < 0.01d,
                        $"lightness {lightness} -> {l2} via {color}");
                }
            }
        }
    }

    [Theory]
    [InlineData(360d)]      // wraps to 0
    [InlineData(720d)]
    [InlineData(-120d)]     // negative wraps forward
    public void FromHsl_normalises_hue_outside_one_turn(double hue)
    {
        var color = ColorMath.FromHsl(hue, 1d, 0.5d);
        var expected = ColorMath.FromHsl(((hue % 360d) + 360d) % 360d, 1d, 0.5d);

        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0d)]
    [InlineData(0xFF, 0xFF, 0xFF, 1d)]
    // The sRGB primaries' luminance coefficients, which is what makes blue so hard
    // to make legible and yellow so hard to keep dark.
    [InlineData(0xFF, 0x00, 0x00, 0.2126d)]
    [InlineData(0x00, 0xFF, 0x00, 0.7152d)]
    [InlineData(0x00, 0x00, 0xFF, 0.0722d)]
    public void RelativeLuminance_matches_the_WCAG_definition(byte r, byte g, byte b, double expected)
    {
        Assert.Equal(expected, ColorMath.RelativeLuminance(Color.FromRgb(r, g, b)), precision: 4);
    }

    [Fact]
    public void ContrastRatio_spans_one_to_twentyone()
    {
        Assert.Equal(21d, ColorMath.ContrastRatio(Colors.Black, Colors.White), precision: 2);
        Assert.Equal(1d, ColorMath.ContrastRatio(Colors.Black, Colors.Black), precision: 6);
        Assert.Equal(1d, ColorMath.ContrastRatio(Colors.White, Colors.White), precision: 6);
    }

    [Fact]
    public void ContrastRatio_is_symmetric()
    {
        var a = Color.FromRgb(0x2C, 0x2C, 0x2C);
        var b = Color.FromRgb(0xDB, 0x61, 0x8D);

        Assert.Equal(ColorMath.ContrastRatio(a, b), ColorMath.ContrastRatio(b, a), precision: 9);
    }
}
