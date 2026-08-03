using System.Diagnostics;
using NAudio.Dsp;
using TaskbarMusicWidget.Diagnostics;
using TaskbarMusicWidget.Settings;

namespace TaskbarMusicWidget.Audio;

/// <summary>
/// Turns a stream of mono samples into a small number of perceptually spaced band
/// levels suitable for driving the visualiser.
/// </summary>
/// <remarks>
/// <para>
/// Bands are log-spaced because pitch is perceived logarithmically; four linear
/// slices of a 24 kHz range would put three of them in territory music barely
/// occupies, and the bars would sit still.
/// </para>
/// <para>
/// Levels are mapped through decibels rather than raw amplitude. Linear amplitude
/// is dominated by bass to the point where the upper bars never move.
/// </para>
/// </remarks>
internal sealed class SpectrumProcessor
{
    /// <summary>1024 samples ≈ 21ms at 48kHz — responsive without being jittery.</summary>
    internal const int FftSize = 1024;
    private const int FftOrder = 10;   // 2^10 == FftSize

    /// <summary>
    /// Span the bars cover. Below 40Hz is mostly rumble the speakers cannot
    /// reproduce; above ~10kHz there is rarely enough energy to move a bar.
    /// </summary>
    private const double LowestHz = 40d;
    private const double HighestHz = 10240d;

    /// <summary>Width of the covered span, in octaves — exactly 8.</summary>
    private const double TotalOctaves = 8d;

    /// <summary>
    /// Where each band's dB window sits, as a function of its centre frequency in
    /// octaves above <see cref="LowestHz"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every band gets its own floor and ceiling rather than sharing one window with
    /// a gain multiplier. Measured against real music the bands sit about 30dB apart
    /// — bass around -28dBFS, treble around -58dBFS — so a shared window pinned the
    /// bass bar near 0.7 with only ~4dB of visible swing while the treble bar
    /// repeatedly bottomed out at zero.
    /// </para>
    /// <para>
    /// These four constants are a least-squares fit through the four windows that
    /// were originally measured by hand, which is what lets the band count vary at
    /// all: a table only describes the count it was measured for. The fit
    /// reproduces the measured windows to within 2dB — a tenth of a window, and
    /// only at one band's floor — so the default four bars behave as before.
    /// </para>
    /// <para>
    /// It generalises to other counts because <see cref="Compute"/> takes the RMS
    /// across a band's bins, which is average power per bin — a spectral density,
    /// not a total. Narrowing a band therefore does not systematically lower its
    /// level, so the same trend line holds however finely the span is cut.
    /// </para>
    /// </remarks>
    private const double FloorDbAtLowest = -37.5d;
    private const double FloorDbPerOctave = -4.5d;
    private const double CeilingDbAtLowest = -18.3d;
    private const double CeilingDbPerOctave = -4.3d;

    private readonly float[] _buffer = new float[FftSize];
    private readonly float[] _hann = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly object _gate = new();

    private Band[] _plan;
    private double[] _bands;
    private double[] _decibels;
    private int _bandCount;

    internal readonly record struct Band(double LowHz, double HighHz, double FloorDb, double CeilingDb);

    // Tuning aid: band levels are only meaningful against real music, so the raw
    // dB and mapped level are sampled periodically when TMW_DEBUG is on.
    private readonly Stopwatch _logThrottle = Stopwatch.StartNew();

    public SpectrumProcessor(int bandCount = WidgetSettings.DefaultBarCount)
    {
        // Hann window: without it, the discontinuity at the frame edges smears
        // energy across every bin and the bands all move together.
        for (int i = 0; i < FftSize; i++)
            _hann[i] = (float)(0.5d * (1d - Math.Cos(2d * Math.PI * i / (FftSize - 1))));

        _bandCount = bandCount;
        _plan = BuildPlan(bandCount);
        _bands = new double[bandCount];
        _decibels = new double[bandCount];
    }

    /// <summary>
    /// How many bands the spectrum is split into. Follows the bar count, so the
    /// bars are always fed a band each.
    /// </summary>
    public int BandCount
    {
        get { lock (_gate) return _bandCount; }
        set
        {
            lock (_gate)
            {
                if (_bandCount == value) return;

                _bandCount = value;
                _plan = BuildPlan(value);
                _bands = new double[value];
                _decibels = new double[value];
            }
        }
    }

    /// <summary>
    /// Divides the covered span into equal slices in octaves, and gives each one a
    /// dB window from the fitted trend.
    /// </summary>
    /// <remarks>
    /// Equal in octaves rather than in hertz because pitch is perceived
    /// logarithmically; linear slices would put all but the first in territory music
    /// barely occupies, and those bars would sit still.
    /// </remarks>
    internal static Band[] BuildPlan(int bandCount)
    {
        var plan = new Band[bandCount];

        for (int b = 0; b < bandCount; b++)
        {
            double lowHz = LowestHz * Math.Pow(2d, TotalOctaves * b / bandCount);
            double highHz = LowestHz * Math.Pow(2d, TotalOctaves * (b + 1) / bandCount);
            double centreOctave = TotalOctaves * (b + 0.5d) / bandCount;

            plan[b] = new Band(
                lowHz,
                highHz,
                FloorDbAtLowest + (FloorDbPerOctave * centreOctave),
                CeilingDbAtLowest + (CeilingDbPerOctave * centreOctave));
        }

        return plan;
    }

    /// <summary>Appends mono samples and recomputes the bands.</summary>
    public void Process(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length == 0 || sampleRate <= 0) return;

        int take = Math.Min(mono.Length, FftSize);

        // Slide the FIFO left and append the newest samples at the end.
        if (take < FftSize)
            Array.Copy(_buffer, take, _buffer, 0, FftSize - take);

        mono[^take..].CopyTo(_buffer.AsSpan(FftSize - take));

        Compute(sampleRate);
    }

    private void Compute(int sampleRate)
    {
        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _buffer[i] * _hann[i];
            _fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftOrder, _fft);

        double binHz = (double)sampleRate / FftSize;
        int nyquistBin = FftSize / 2;

        lock (_gate)
        {
            for (int b = 0; b < _bandCount; b++)
            {
                var (lowHz, highHz, floorDb, ceilingDb) = _plan[b];

                int lowBin = Math.Max(1, (int)(lowHz / binHz));
                int highBin = Math.Min(nyquistBin - 1, (int)(highHz / binHz));
                if (highBin < lowBin) highBin = lowBin;

                // RMS across the band, so a band's level reflects its total energy
                // rather than whichever single bin happens to spike.
                double sum = 0d;
                for (int i = lowBin; i <= highBin; i++)
                {
                    double re = _fft[i].X;
                    double im = _fft[i].Y;
                    sum += (re * re) + (im * im);
                }

                double rms = Math.Sqrt(sum / (highBin - lowBin + 1));
                double db = 20d * Math.Log10(rms + 1e-12d);

                double level = (db - floorDb) / (ceilingDb - floorDb);
                _decibels[b] = db;
                _bands[b] = Math.Clamp(level, 0d, 1d);
            }

            if (_logThrottle.ElapsedMilliseconds >= 400)
            {
                _logThrottle.Restart();
                DebugLog.Write(
                    $"bands dB=[{string.Join(' ', _decibels.Select(d => d.ToString("F1").PadLeft(6)))}] " +
                    $"lvl=[{string.Join(' ', _bands.Select(l => l.ToString("F2")))}]");
            }
        }
    }

    /// <summary>Copies the most recent band levels. Safe to call from the render thread.</summary>
    public void CopyTo(Span<double> destination)
    {
        lock (_gate)
        {
            int n = Math.Min(destination.Length, _bandCount);
            for (int i = 0; i < n; i++)
                destination[i] = _bands[i];
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_bands);
            Array.Clear(_buffer);
        }
    }
}
