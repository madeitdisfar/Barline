using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarMusicWidget.Ui;
using Xunit;

namespace TaskbarMusicWidget.Tests;

/// <summary>
/// Hue extraction from cover art.
/// </summary>
/// <remarks>
/// Fixtures are built as raw BGRA32 pixel buffers rather than rendered through a
/// <c>DrawingVisual</c>: it gives exact control over what the sampler sees, and it
/// avoids needing a WPF visual tree (and its thread affinity) inside a test run.
/// </remarks>
public class AlbumArtPaletteTests
{
    [Theory]
    // Hue of each source, so the assertion is not restating a magic number.
    [InlineData(0xDC, 0x14, 0x3C, 348d)]    // crimson
    [InlineData(0x1A, 0x23, 0x7E, 235d)]    // navy
    [InlineData(0x22, 0x8B, 0x22, 120d)]    // forest green
    [InlineData(0xFF, 0xA5, 0x00, 39d)]     // orange
    [InlineData(0x4B, 0x00, 0x82, 275d)]    // indigo
    public void A_solid_cover_yields_its_own_hue(byte r, byte g, byte b, double expectedHue)
    {
        var art = Solid(64, 64, Color.FromRgb(r, g, b));

        var seed = AlbumArtPalette.TryExtractSeed(art);

        Assert.NotNull(seed);
        var (hue, _, _) = ColorMath.ToHsl(seed.Value);
        Assert.True(HueDrift(hue, expectedHue) < 8d,
            $"expected hue near {expectedHue}, got {hue:F1}");
    }

    [Fact]
    public void The_seed_normalises_lightness_and_carries_only_hue_and_saturation()
    {
        // Documented contract: choosing a final lightness belongs to Legibility,
        // because it depends on the taskbar rather than on the artwork.
        var art = Solid(64, 64, Color.FromRgb(0x1A, 0x23, 0x7E));   // a dark navy

        var seed = AlbumArtPalette.TryExtractSeed(art);

        Assert.NotNull(seed);
        var (_, saturation, lightness) = ColorMath.ToHsl(seed.Value);

        Assert.Equal(0.5d, lightness, precision: 1);
        Assert.True(saturation > 0.2d, $"saturation collapsed to {saturation:F2}");
    }

    [Fact]
    public void The_dominant_hue_wins_over_a_minority_one()
    {
        var blue = Color.FromRgb(0x20, 0x60, 0xD0);
        var red = Color.FromRgb(0xD0, 0x20, 0x20);

        // Three quarters blue, one quarter red.
        var art = Pixels(64, 64, (_, y) => y < 48 ? blue : red);

        var seed = AlbumArtPalette.TryExtractSeed(art);

        Assert.NotNull(seed);
        var (hue, _, _) = ColorMath.ToHsl(seed.Value);
        var (blueHue, _, _) = ColorMath.ToHsl(blue);

        Assert.True(HueDrift(hue, blueHue) < 15d,
            $"expected the blue majority near {blueHue:F1}, got {hue:F1}");
    }

    [Fact]
    public void Letterboxing_does_not_outvote_the_actual_artwork()
    {
        // Two thirds of this cover is pure black, which would win on raw pixel count.
        // The lightness gate is what stops it, and without that gate almost every
        // letterboxed or vignetted cover would resolve to no hue at all.
        var orange = Color.FromRgb(0xFF, 0x8C, 0x00);
        var art = Pixels(64, 64, (_, y) => y is >= 22 and < 42 ? orange : Colors.Black);

        var seed = AlbumArtPalette.TryExtractSeed(art);

        Assert.NotNull(seed);
        var (hue, _, _) = ColorMath.ToHsl(seed.Value);
        var (orangeHue, _, _) = ColorMath.ToHsl(orange);

        Assert.True(HueDrift(hue, orangeHue) < 12d,
            $"expected the orange band near {orangeHue:F1}, got {hue:F1}");
    }

    /// <summary>
    /// A very dark cover with a small bright accent must resolve to the accent.
    /// </summary>
    /// <remarks>
    /// This is what the lightness gate is for, as distinct from the saturation gate
    /// above: these shadow pixels are strongly saturated, so only their lightness
    /// disqualifies them. They also could not be drawn as-is — corrected for contrast,
    /// a near-black navy becomes a bright blue that nobody looking at the sleeve would
    /// say was its colour, while the orange genuinely is.
    /// </remarks>
    [Fact]
    public void A_near_black_majority_does_not_outvote_a_bright_accent()
    {
        var shadow = Color.FromRgb(0x04, 0x05, 0x14);   // dark, but ~0.6 saturation
        var accent = Color.FromRgb(0xFF, 0x8C, 0x00);

        // Only the bottom ~5% carries the accent.
        var art = Pixels(64, 64, (_, y) => y < 61 ? shadow : accent);

        var seed = AlbumArtPalette.TryExtractSeed(art);

        Assert.NotNull(seed);
        var (hue, _, _) = ColorMath.ToHsl(seed.Value);
        var (accentHue, _, _) = ColorMath.ToHsl(accent);

        Assert.True(HueDrift(hue, accentHue) < 15d,
            $"expected the accent near {accentHue:F1}, got {hue:F1}");
    }

    [Fact]
    public void A_greyscale_cover_yields_no_seed()
    {
        // Inventing a hue here would show the user a colour that is not in their
        // artwork; the caller falls back to the theme colour instead.
        var art = Pixels(64, 64, (x, _) =>
        {
            byte level = (byte)(x * 255 / 63);
            return Color.FromRgb(level, level, level);
        });

        Assert.Null(AlbumArtPalette.TryExtractSeed(art));
    }

    [Theory]
    [InlineData(0xFF, 0xFF, 0xFF)]      // pure white
    [InlineData(0x00, 0x00, 0x00)]      // pure black
    [InlineData(0x77, 0x77, 0x77)]      // mid grey
    public void A_flat_neutral_cover_yields_no_seed(byte r, byte g, byte b)
    {
        Assert.Null(AlbumArtPalette.TryExtractSeed(Solid(64, 64, Color.FromRgb(r, g, b))));
    }

    [Fact]
    public void A_fully_transparent_cover_yields_no_seed()
    {
        var art = Solid(64, 64, Color.FromArgb(0x00, 0xFF, 0x00, 0x00));

        Assert.Null(AlbumArtPalette.TryExtractSeed(art));
    }

    [Fact]
    public void Missing_art_yields_no_seed()
    {
        Assert.Null(AlbumArtPalette.TryExtractSeed(null));
    }

    [Fact]
    public void A_cover_smaller_than_the_sample_size_is_handled()
    {
        // Art below the sample edge skips the downscale path entirely; SMTC thumbnails
        // from some sources really are this small.
        var art = Solid(8, 8, Color.FromRgb(0x22, 0x8B, 0x22));

        var seed = AlbumArtPalette.TryExtractSeed(art);

        Assert.NotNull(seed);
        var (hue, _, _) = ColorMath.ToHsl(seed.Value);
        Assert.True(HueDrift(hue, 120d) < 8d, $"expected green near 120, got {hue:F1}");
    }

    [Fact]
    public void An_extracted_hue_survives_correction_for_both_taskbars()
    {
        // The end-to-end path: whatever the artwork gives has to come out legible.
        var art = Solid(64, 64, Color.FromRgb(0x1A, 0x23, 0x7E));    // dark navy, worst case

        var seed = AlbumArtPalette.TryExtractSeed(art);
        Assert.NotNull(seed);

        var onDark = Legibility.TryCoerce(seed.Value, Theme.DarkBackdrop, preferLighter: true);
        var onLight = Legibility.TryCoerce(seed.Value, Theme.LightBackdrop, preferLighter: false);

        Assert.NotNull(onDark);
        Assert.NotNull(onLight);
        Assert.True(ColorMath.ContrastRatio(onDark.Value, Theme.DarkBackdrop) >= Legibility.MinContrast);
        Assert.True(ColorMath.ContrastRatio(onLight.Value, Theme.LightBackdrop) >= Legibility.MinContrast);
    }

    // ---- fixtures ----------------------------------------------------------

    private static double HueDrift(double a, double b) =>
        Math.Abs(((a - b + 540d) % 360d) - 180d);

    private static BitmapSource Solid(int width, int height, Color color) =>
        Pixels(width, height, (_, _) => color);

    private static BitmapSource Pixels(int width, int height, Func<int, int, Color> pixel)
    {
        int stride = width * 4;
        var buffer = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var color = pixel(x, y);
                int i = y * stride + x * 4;

                // Bgra32 is straight alpha, not premultiplied.
                buffer[i + 0] = color.B;
                buffer[i + 1] = color.G;
                buffer[i + 2] = color.R;
                buffer[i + 3] = color.A;
            }
        }

        var bitmap = BitmapSource.Create(
            width, height, 96d, 96d, PixelFormats.Bgra32, null, buffer, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
