using System.Diagnostics;
using NAudio.Dsp;
using TaskbarMusicWidget.Diagnostics;

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
    public const int BandCount = 4;

    /// <summary>1024 samples ≈ 21ms at 48kHz — responsive without being jittery.</summary>
    private const int FftSize = 1024;
    private const int FftOrder = 10;   // 2^10 == FftSize

    /// <summary>
    /// Frequency span and dB window for each bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every band gets its own floor and ceiling rather than sharing one window
    /// with a gain multiplier. Measured against real music, the bands sit about
    /// 30dB apart — bass around -28dBFS, treble around -58dBFS — so a shared
    /// window pinned the bass bar near 0.7 with only ~4dB of visible swing while
    /// the treble bar repeatedly bottomed out at zero.
    /// </para>
    /// <para>
    /// Each window is centred on that band's typical level and kept deliberately
    /// narrow, so ordinary programme material spans most of the bar's travel
    /// instead of a sliver of it. Silence still falls below every floor and lets
    /// all four settle to rest.
    /// </para>
    /// </remarks>
    private static readonly (double LowHz, double HighHz, double FloorDb, double CeilingDb)[] Bands =
    [
        (40d, 160d, -42d, -22d),
        (160d, 640d, -50d, -32d),
        (640d, 2560d, -62d, -40d),
        (2560d, 10240d, -68d, -48d),
    ];

    private readonly float[] _buffer = new float[FftSize];
    private readonly float[] _hann = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly double[] _bands = new double[BandCount];
    private readonly double[] _decibels = new double[BandCount];
    private readonly object _gate = new();

    // Tuning aid: band levels are only meaningful against real music, so the raw
    // dB and mapped level are sampled periodically when TMW_DEBUG is on.
    private readonly Stopwatch _logThrottle = Stopwatch.StartNew();

    public SpectrumProcessor()
    {
        // Hann window: without it, the discontinuity at the frame edges smears
        // energy across every bin and the bands all move together.
        for (int i = 0; i < FftSize; i++)
            _hann[i] = (float)(0.5d * (1d - Math.Cos(2d * Math.PI * i / (FftSize - 1))));
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
            for (int b = 0; b < BandCount; b++)
            {
                var (lowHz, highHz, floorDb, ceilingDb) = Bands[b];

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
                DebugLog.Write(string.Format(
                    "bands dB=[{0,6:F1} {1,6:F1} {2,6:F1} {3,6:F1}] lvl=[{4:F2} {5:F2} {6:F2} {7:F2}]",
                    _decibels[0], _decibels[1], _decibels[2], _decibels[3],
                    _bands[0], _bands[1], _bands[2], _bands[3]));
            }
        }
    }

    /// <summary>Copies the most recent band levels. Safe to call from the render thread.</summary>
    public void CopyTo(Span<double> destination)
    {
        lock (_gate)
        {
            int n = Math.Min(destination.Length, BandCount);
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
