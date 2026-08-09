using Barline.Audio;
using Barline.Settings;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// The band plan behind the bar-count setting.
/// </summary>
/// <remarks>
/// The original four dB windows were measured by hand against real music, which meant
/// they described one count and no other. Replacing the table with a fitted trend is
/// what lets the count vary — so these tests hold the fit to the measurements it came
/// from, and hold the range to what the transform can actually resolve.
/// </remarks>
public class BandPlanTests
{
    /// <summary>
    /// The four windows the fit was derived from, measured against real programme
    /// material. Ground truth: the fit serves these, not the other way round.
    /// </summary>
    private static readonly (double LowHz, double HighHz, double FloorDb, double CeilingDb)[] Measured =
    [
        (40d, 160d, -42d, -22d),
        (160d, 640d, -50d, -32d),
        (640d, 2560d, -62d, -40d),
        (2560d, 10240d, -68d, -48d),
    ];

    /// <summary>
    /// How far the fitted windows may sit from the measured ones. Two decibels is a
    /// tenth of a window, and only one band's floor is that far out.
    /// </summary>
    private const double ToleranceDb = 2d;

    public static TheoryData<int> SupportedCounts
    {
        get
        {
            var data = new TheoryData<int>();
            for (int count = WidgetSettings.MinBarCount; count <= WidgetSettings.MaxBarCount; count++)
                data.Add(count);
            return data;
        }
    }

    [Fact]
    public void The_default_plan_still_matches_the_windows_that_were_measured()
    {
        var plan = SpectrumProcessor.BuildPlan(WidgetSettings.DefaultBarCount);

        Assert.Equal(Measured.Length, plan.Length);

        for (int b = 0; b < plan.Length; b++)
        {
            Assert.Equal(Measured[b].LowHz, plan[b].LowHz, precision: 6);
            Assert.Equal(Measured[b].HighHz, plan[b].HighHz, precision: 6);

            Assert.True(
                Math.Abs(plan[b].FloorDb - Measured[b].FloorDb) <= ToleranceDb + 1e-9,
                $"band {b} floor drifted to {plan[b].FloorDb:F1} from {Measured[b].FloorDb:F1}");

            Assert.True(
                Math.Abs(plan[b].CeilingDb - Measured[b].CeilingDb) <= ToleranceDb + 1e-9,
                $"band {b} ceiling drifted to {plan[b].CeilingDb:F1} from {Measured[b].CeilingDb:F1}");
        }
    }

    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void The_bands_tile_the_covered_span_without_gaps(int count)
    {
        var plan = SpectrumProcessor.BuildPlan(count);

        Assert.Equal(count, plan.Length);
        Assert.Equal(40d, plan[0].LowHz, precision: 6);
        Assert.Equal(10240d, plan[^1].HighHz, precision: 6);

        for (int b = 1; b < plan.Length; b++)
            Assert.Equal(plan[b - 1].HighHz, plan[b].LowHz, precision: 6);
    }

    /// <summary>
    /// Equal in octaves, not in hertz. Linear slices would put every band but the
    /// first in territory music barely occupies, and those bars would sit still.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void Every_band_spans_the_same_number_of_octaves(int count)
    {
        var plan = SpectrumProcessor.BuildPlan(count);
        double expected = 8d / count;

        foreach (var band in plan)
            Assert.Equal(expected, Math.Log2(band.HighHz / band.LowHz), precision: 6);
    }

    /// <summary>
    /// Real music loses roughly 4.4dB per octave, which is the whole reason each band
    /// gets its own window rather than sharing one with a gain multiplier.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void Higher_bands_sit_at_quieter_windows(int count)
    {
        var plan = SpectrumProcessor.BuildPlan(count);

        for (int b = 1; b < plan.Length; b++)
        {
            Assert.True(plan[b].FloorDb < plan[b - 1].FloorDb, $"band {b} floor did not fall");
            Assert.True(plan[b].CeilingDb < plan[b - 1].CeilingDb, $"band {b} ceiling did not fall");
        }
    }

    /// <summary>
    /// A window narrow enough that ordinary material spans most of the bar's travel,
    /// and wide enough that it does not clip at both ends.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void Every_window_stays_about_twenty_decibels_wide(int count)
    {
        foreach (var band in SpectrumProcessor.BuildPlan(count))
        {
            double width = band.CeilingDb - band.FloorDb;
            Assert.InRange(width, 18d, 22d);
        }
    }

    /// <summary>
    /// The constraint that set the ceiling on the range. A band whose bins are all
    /// shared with its neighbor carries no information of its own, and its bar would
    /// move in lockstep with the one beside it.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedCounts))]
    public void Every_supported_count_gives_each_band_a_bin_of_its_own(int count)
    {
        foreach (int sampleRate in new[] { 44100, 48000 })
        {
            var ranges = BinRanges(count, sampleRate);

            for (int b = 0; b < ranges.Length; b++)
            {
                Assert.True(
                    HasExclusiveBin(ranges, b),
                    $"at {count} bands and {sampleRate}Hz, band {b} shares every bin with a neighbor");
            }
        }
    }

    /// <summary>
    /// Why the range stops at six, asserted rather than asserted-in-a-comment. At
    /// seven bands and 48kHz the lowest band spans 40Hz to 89.8Hz, which is inside a
    /// single 46.9Hz bin that the second band also starts from — so the bottom two
    /// bars would carry identical data and move as one.
    /// </summary>
    [Fact]
    public void Seven_bands_is_past_what_the_transform_can_resolve()
    {
        var ranges = BinRanges(WidgetSettings.MaxBarCount + 1, 48000);

        Assert.False(
            HasExclusiveBin(ranges, 0),
            "seven bands now resolve; the range ceiling can be reconsidered");
    }

    /// <summary>Mirrors the bin selection in <c>SpectrumProcessor.Compute</c>.</summary>
    private static (int Low, int High)[] BinRanges(int count, int sampleRate)
    {
        var plan = SpectrumProcessor.BuildPlan(count);
        double binHz = (double)sampleRate / SpectrumProcessor.FftSize;
        int nyquistBin = SpectrumProcessor.FftSize / 2;

        var ranges = new (int Low, int High)[plan.Length];

        for (int b = 0; b < plan.Length; b++)
        {
            int low = Math.Max(1, (int)(plan[b].LowHz / binHz));
            int high = Math.Min(nyquistBin - 1, (int)(plan[b].HighHz / binHz));
            if (high < low) high = low;

            ranges[b] = (low, high);
        }

        return ranges;
    }

    private static bool HasExclusiveBin((int Low, int High)[] ranges, int index)
    {
        for (int bin = ranges[index].Low; bin <= ranges[index].High; bin++)
        {
            bool shared = false;

            for (int other = 0; other < ranges.Length && !shared; other++)
            {
                if (other == index) continue;
                shared = bin >= ranges[other].Low && bin <= ranges[other].High;
            }

            if (!shared) return true;
        }

        return false;
    }
}
