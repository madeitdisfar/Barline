using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TaskbarMusicWidget.Diagnostics;

namespace TaskbarMusicWidget.Audio;

/// <summary>
/// Captures the system audio mix via WASAPI loopback and exposes it as smoothed
/// band levels for the visualiser.
/// </summary>
/// <remarks>
/// <para>
/// Loopback captures everything the default output device is playing, not just the
/// media session the widget is showing. Per-process loopback exists but needs a
/// much heavier activation path, so the whole mix is used here.
/// </para>
/// <para>
/// Two failure modes matter and are handled explicitly. WASAPI raises no
/// <c>DataAvailable</c> at all during true silence, so staleness is treated as
/// silence rather than leaving the bars frozen mid-motion. And switching output
/// device (Bluetooth, headphones) stops the capture outright, so it is re-armed.
/// </para>
/// </remarks>
internal sealed class LoopbackAnalyzer : IDisposable
{
    /// <summary>Beyond this with no callback, treat the system as silent.</summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMilliseconds(300);

    private readonly SpectrumProcessor _processor = new();
    private readonly Stopwatch _sinceData = Stopwatch.StartNew();
    private readonly object _lifecycle = new();

    private WasapiLoopbackCapture? _capture;
    private float[] _mono = new float[8192];
    private bool _restartPending;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed || IsRunning) return;

            try
            {
                _capture = new WasapiLoopbackCapture();
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                IsRunning = true;
                _sinceData.Restart();

                var wf = _capture.WaveFormat;
                DebugLog.Write($"loopback started: {wf.SampleRate}Hz {wf.Channels}ch {wf.Encoding} {wf.BitsPerSample}bit");
            }
            catch (Exception ex)
            {
                // No audio endpoint, or the device is in exclusive mode. The
                // visualiser falls back to its decorative motion.
                DebugLog.Write($"loopback unavailable: {ex.Message}");
                DisposeCapture();
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (!IsRunning) return;
            try { _capture?.StopRecording(); }
            catch (Exception ex) { DebugLog.Write($"loopback stop failed: {ex.Message}"); }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null || e.BytesRecorded <= 0) return;

        var format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels);

        int frames = ToMono(e.Buffer.AsSpan(0, e.BytesRecorded), format, channels);
        if (frames <= 0) return;

        _sinceData.Restart();
        _processor.Process(_mono.AsSpan(0, frames), format.SampleRate);
    }

    /// <summary>Downmixes the interleaved capture buffer into <see cref="_mono"/>.</summary>
    /// <returns>Number of mono frames written.</returns>
    private int ToMono(ReadOnlySpan<byte> buffer, WaveFormat format, int channels)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0) return 0;

        int frames = buffer.Length / (bytesPerSample * channels);
        if (frames <= 0) return 0;

        if (frames > _mono.Length)
            _mono = new float[frames];

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                int baseIndex = f * channels;
                for (int c = 0; c < channels; c++)
                    sum += samples[baseIndex + c];
                _mono[f] = sum / channels;
            }
            return frames;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                int baseIndex = f * channels;
                for (int c = 0; c < channels; c++)
                    sum += samples[baseIndex + c] / 32768f;
                _mono[f] = sum / channels;
            }
            return frames;
        }

        return 0;   // unexpected mix format; decorative fallback covers it
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        DebugLog.Write($"loopback stopped{(e.Exception is null ? string.Empty : $": {e.Exception.Message}")}");

        lock (_lifecycle)
        {
            DisposeCapture();
            if (_disposed || _restartPending) return;

            // A stop almost always means the default output device changed.
            // Re-arm after a short delay so the new endpoint has settled.
            _restartPending = true;
        }

        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ =>
        {
            lock (_lifecycle) { _restartPending = false; }
            if (!_disposed) Start();
        });
    }

    /// <summary>
    /// Copies current band levels for the render loop.
    /// </summary>
    /// <returns>
    /// False when capture is unavailable or the mix has gone silent, which tells
    /// the visualiser to fall back to its decorative motion.
    /// </returns>
    public bool TryGetLevels(Span<double> destination)
    {
        if (!IsRunning) return false;

        if (_sinceData.Elapsed > SilenceTimeout)
        {
            // Genuine silence produces no callbacks at all, so report zeros and
            // let the bars ease down rather than holding their last position.
            destination.Clear();
            return true;
        }

        _processor.CopyTo(destination);
        return true;
    }

    private void DisposeCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { /* endpoint already gone */ }
            _capture = null;
        }

        IsRunning = false;
        _processor.Reset();
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            try { _capture?.StopRecording(); } catch { /* ignore */ }
            DisposeCapture();
        }
    }
}
