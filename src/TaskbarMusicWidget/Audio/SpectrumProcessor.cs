using NAudio.Dsp;

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

    /// <summary>Bottom of the useful dynamic range, in dBFS.</summary>
    private const double FloorDb = -62d;
    private const double CeilingDb = -12d;

    // Music has a roughly pink spectrum: energy falls as frequency rises. Without
    // per-band gain the treble bar would barely register next to the bass one.
    private static readonly (double LowHz, double HighHz, double Gain)[] Bands =
    [
        (40d, 160d, 1.00d),
        (160d, 640d, 1.20d),
        (640d, 2560d, 1.55d),
        (2560d, 10240d, 2.00d),
    ];

    private readonly float[] _buffer = new float[FftSize];
    private readonly float[] _hann = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly double[] _bands = new double[BandCount];
    private readonly object _gate = new();

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
                var (lowHz, highHz, gain) = Bands[b];

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

                double level = (db - FloorDb) / (CeilingDb - FloorDb);
                _bands[b] = Math.Clamp(level * gain, 0d, 1d);
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
