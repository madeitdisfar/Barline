using System.Windows.Media;
using System.Windows.Media.Imaging;
using Barline.Diagnostics;

namespace Barline.Ui;

/// <summary>
/// Picks the hue that best represents a piece of album art.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not "the average colour" — averaging a cover mixes complementary
/// regions into mud, and the mud is usually grey. Instead pixels vote into hue
/// buckets, weighted so that vivid mid-tones count for far more than washed-out or
/// nearly-black ones, and the winning cluster's hue is returned.
/// </para>
/// <para>
/// The result is a <em>seed</em>: hue and saturation are meaningful, lightness is
/// normalised to 0.5 and carries no information. Choosing a final lightness is
/// <see cref="BarColorResolver"/>'s job, because it depends on the taskbar the bars
/// have to be legible against, not on the artwork.
/// </para>
/// <para>
/// Returns null when the art has no hue worth using — a black-and-white or sepia
/// cover — rather than amplifying noise into a confident-looking wrong tint.
/// </para>
/// </remarks>
internal static class AlbumArtPalette
{
    /// <summary>
    /// Art is scaled to this edge length before sampling. A thousand-odd pixels is
    /// plenty to find a dominant hue, and it keeps extraction cheap enough to run
    /// inline on a track change.
    /// </summary>
    private const int SampleEdge = 32;

    private const int BucketCount = 24;              // 15° per bucket
    private const double BucketWidth = 360d / BucketCount;

    // Outside this band a pixel carries no usable hue. Both extremes are common on
    // covers — letterboxing, blown highlights — and would otherwise win on count.
    private const double MinLightness = 0.10d;
    private const double MaxLightness = 0.93d;

    /// <summary>Below this a pixel is effectively grey and its hue is noise.</summary>
    private const double MinSaturation = 0.15d;

    /// <summary>
    /// Weight the winning cluster must reach, as a fraction of sampled pixels. Since
    /// a moderately colourful pixel contributes well under 1, this is not a pixel
    /// percentage — it is tuned so that a greyscale cover fails and a pastel one
    /// still passes.
    /// </summary>
    private const double MinClusterWeight = 0.01d;

    public static Color? TryExtractSeed(ImageSource? art)
    {
        // Covers everything the app actually produces (BitmapImage from SMTC,
        // RenderTargetBitmap in demo mode). A DrawingImage would need rasterising
        // first, and nothing supplies one.
        if (art is not BitmapSource bitmap) return null;

        try
        {
            byte[]? pixels = TrySample(bitmap, out int sampled);
            if (pixels is null || sampled <= 0) return null;

            return Vote(pixels, sampled);
        }
        catch (Exception ex)
        {
            // A cover that cannot be sampled is not worth failing over; the caller
            // falls back to the theme colour.
            DebugLog.Write($"album art palette failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Scales the art down and returns its pixels as BGRA32.</summary>
    private static byte[]? TrySample(BitmapSource bitmap, out int sampled)
    {
        sampled = 0;
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;

        BitmapSource source = bitmap;

        double scale = Math.Min(
            (double)SampleEdge / bitmap.PixelWidth,
            (double)SampleEdge / bitmap.PixelHeight);

        // Only ever downscale. Enlarging a tiny thumbnail invents no new information
        // and just multiplies the work.
        if (scale < 1d)
            source = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));

        // Normalising the format means one unpacking path regardless of what the
        // source app handed us, and un-premultiplies Pbgra32 (demo art) on the way.
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0d);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        if (width <= 0 || height <= 0) return null;

        int stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        sampled = width * height;
        return pixels;
    }

    private static Color? Vote(byte[] pixels, int sampled)
    {
        var weights = new double[BucketCount];
        var hueSums = new double[BucketCount];
        var saturationSums = new double[BucketCount];

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha < 200) continue;                       // effectively transparent

            var (hue, saturation, lightness) =
                ColorMath.ToHsl(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]));

            if (saturation < MinSaturation) continue;
            if (lightness < MinLightness || lightness > MaxLightness) continue;

            // Squaring saturation makes a genuinely vivid pixel worth several
            // borderline ones; midness keeps mid-tones ahead of near-black and
            // near-white pixels that scraped past the lightness gate.
            double midness = 1d - Math.Abs(lightness - 0.5d) * 2d;
            double weight = saturation * saturation * (0.35d + 0.65d * midness);

            int bucket = (int)(hue / BucketWidth) % BucketCount;

            weights[bucket] += weight;
            // Stored relative to the bucket's own start so averaging within a bucket
            // cannot be broken by the 360°/0° wrap.
            hueSums[bucket] += (hue - bucket * BucketWidth) * weight;
            saturationSums[bucket] += saturation * weight;
        }

        return Winner(weights, hueSums, saturationSums, sampled);
    }

    /// <summary>
    /// Finds the strongest hue cluster and averages it into a seed colour.
    /// </summary>
    /// <remarks>
    /// Each bucket is scored together with its two neighbours: a real hue cluster is
    /// wider than 15° and frequently straddles a boundary, so scoring buckets in
    /// isolation can split one dominant colour into two losing halves.
    /// </remarks>
    private static Color? Winner(
        double[] weights, double[] hueSums, double[] saturationSums, int sampled)
    {
        int best = -1;
        double bestWeight = 0d;

        for (int b = 0; b < BucketCount; b++)
        {
            int left = (b + BucketCount - 1) % BucketCount;
            int right = (b + 1) % BucketCount;

            double combined = weights[left] + weights[b] + weights[right];
            if (combined > bestWeight)
            {
                bestWeight = combined;
                best = b;
            }
        }

        if (best < 0 || bestWeight < sampled * MinClusterWeight)
        {
            DebugLog.Write(
                $"album art palette: no dominant hue (weight {bestWeight:F2} over {sampled} px)");
            return null;
        }

        {
            int left = (best + BucketCount - 1) % BucketCount;
            int right = (best + 1) % BucketCount;

            // Re-base the neighbours onto the winning bucket's start before summing.
            // Their stored offsets are relative to their own starts, one bucket away.
            double hueSum =
                hueSums[left] - BucketWidth * weights[left] +
                hueSums[best] +
                hueSums[right] + BucketWidth * weights[right];

            double saturationSum =
                saturationSums[left] + saturationSums[best] + saturationSums[right];

            double hue = best * BucketWidth + hueSum / bestWeight;
            double saturation = saturationSum / bestWeight;

            // Lightness is a placeholder — the resolver replaces it outright.
            return ColorMath.FromHsl(hue, saturation, 0.5d);
        }
    }
}
