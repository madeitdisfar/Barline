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
/// Several failure modes matter. WASAPI raises no <c>DataAvailable</c> at all
/// during true silence, so staleness is treated as silence rather than freezing
/// the bars. A capture stays bound to the endpoint it was created on and does NOT
/// follow the default output as it moves, so when headphones reconnect after sleep
/// the capture keeps listening to the old, now-silent device. And the capture can
/// die outright, or silently stall without raising
/// <see cref="WasapiLoopbackCapture.RecordingStopped"/> at all.
/// </para>
/// <para>
/// A watchdog covers all of these: it re-arms the capture when it has died, when
/// the default output device has moved away from the one it is bound to, or when it
/// has gone quiet while audio is known to be playing.
/// </para>
/// </remarks>
internal sealed class LoopbackAnalyzer : IDisposable
{
    /// <summary>Beyond this with no callback, treat the system as silent.</summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the watchdog checks capture health.</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// While audio is expected, no callbacks for this long means the capture has
    /// stalled and must be re-armed. Comfortably longer than any real gap between
    /// samples so ordinary playback never trips it.
    /// </summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(4);

    private readonly SpectrumProcessor _processor = new();
    private readonly Stopwatch _sinceData = Stopwatch.StartNew();
    private readonly object _lifecycle = new();

    private WasapiLoopbackCapture? _capture;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _captureDevice;
    private string? _captureDeviceId;
    private System.Threading.Timer? _watchdog;
    private float[] _mono = new float[8192];
    private bool _shouldRun;
    private bool _hasCapturedData;
    private bool _disposed;

    private MMDeviceEnumerator Enumerator => _deviceEnumerator ??= new MMDeviceEnumerator();

    public bool IsRunning { get; private set; }

    /// <summary>
    /// How many bands the spectrum is split into. Follows the visualiser's bar
    /// count, so every bar is fed by a band of its own.
    /// </summary>
    public int BandCount
    {
        get => _processor.BandCount;
        set => _processor.BandCount = value;
    }

    /// <summary>
    /// Set by the UI to say whether the widget believes audio is playing. It lets
    /// the watchdog tell a genuine stall (playing, but no callbacks) apart from
    /// ordinary silence (paused, and no callbacks is expected).
    /// </summary>
    public bool ExpectingAudio { get; set; }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _shouldRun = true;
            _watchdog ??= new System.Threading.Timer(
                _ => Watchdog(), null, WatchdogInterval, WatchdogInterval);

            if (IsRunning) return;
            StartCaptureLocked();
        }
    }

    /// <summary>Tears down and re-arms the capture. Safe to call from any thread.</summary>
    public void Restart()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            DebugLog.Write("loopback: manual restart");
            _shouldRun = true;
            RestartCaptureLocked();
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            _shouldRun = false;
            if (!IsRunning) return;
            try { _capture?.StopRecording(); }
            catch (Exception ex) { DebugLog.Write($"loopback stop failed: {ex.Message}"); }
        }
    }

    // Assumes _lifecycle is held.
    private void StartCaptureLocked()
    {
        try
        {
            // Bind to an explicit device (the current default) rather than the
            // default-constructor default, so we know which endpoint we are on and
            // can notice when the default later moves elsewhere.
            _captureDevice = TryGetDefaultRenderDevice();
            if (_captureDevice is null)
            {
                DebugLog.Write("loopback: no default render device; will retry");
                DisposeCapture();
                return;
            }

            _captureDeviceId = _captureDevice.ID;
            _capture = new WasapiLoopbackCapture(_captureDevice);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            IsRunning = true;
            _hasCapturedData = false;
            _sinceData.Restart();

            var wf = _capture.WaveFormat;
            DebugLog.Write(
                $"loopback started on '{_captureDevice.FriendlyName}': " +
                $"{wf.SampleRate}Hz {wf.Channels}ch {wf.Encoding} {wf.BitsPerSample}bit");
        }
        catch (Exception ex)
        {
            // No audio endpoint, or the device is in exclusive mode. The
            // visualiser falls back to its decorative motion; the watchdog retries.
            DebugLog.Write($"loopback unavailable: {ex.Message}");
            DisposeCapture();
        }
    }

    private MMDevice? TryGetDefaultRenderDevice()
    {
        try
        {
            return Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"default render device query failed: {ex.Message}");
            return null;
        }
    }

    // Assumes _lifecycle is held.
    private void RestartCaptureLocked()
    {
        DisposeCapture();
        StartCaptureLocked();
    }

    /// <summary>
    /// Periodic health check. Re-arms a capture that has died, that is bound to a
    /// device the default has since moved away from, or that has gone quiet while
    /// audio is expected. When no audio is expected, quiet is normal and left alone
    /// so an idle widget does not churn the audio device.
    /// </summary>
    private void Watchdog()
    {
        lock (_lifecycle)
        {
            if (_disposed || !_shouldRun) return;

            if (!IsRunning)
            {
                DebugLog.Write("watchdog: capture not running; re-arming");
                StartCaptureLocked();
                return;
            }

            // Follow the default device. WasapiLoopbackCapture stays bound to the
            // endpoint it was created on, so when the default output moves — e.g.
            // headphones reconnect after sleep — the capture keeps listening to the
            // old (now silent) device and the visualiser goes dead. Re-arm onto the
            // new default. This is independent of the stall check below, which the
            // wrong-device case would never satisfy (that capture never had data).
            string? currentDefaultId = TryGetDefaultRenderId();
            if (currentDefaultId is not null &&
                _captureDeviceId is not null &&
                !string.Equals(currentDefaultId, _captureDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                DebugLog.Write("watchdog: default render device changed; re-arming onto it");
                RestartCaptureLocked();
                return;
            }

            // Only a capture that HAS been delivering data and then went quiet
            // while audio is still expected counts as a stall. A capture that has
            // never produced data is either brand new or on a silent system, and
            // re-arming it would just churn without fixing anything.
            if (ExpectingAudio && _hasCapturedData && _sinceData.Elapsed > StallThreshold)
            {
                DebugLog.Write(
                    $"watchdog: stalled {_sinceData.Elapsed.TotalSeconds:F1}s while audio expected; re-arming");
                RestartCaptureLocked();
            }
        }
    }

    private string? TryGetDefaultRenderId()
    {
        try
        {
            using var device = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"default render id query failed: {ex.Message}");
            return null;
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

        _hasCapturedData = true;
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

        // Just tear down here. The watchdog re-arms on its next tick if we should
        // still be running, which also gives the new default device time to settle
        // and retries automatically if the first attempt fails.
        lock (_lifecycle)
        {
            DisposeCapture();
        }
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

        // The capture holds the device open, so release it only after the capture.
        if (_captureDevice is not null)
        {
            try { _captureDevice.Dispose(); } catch { /* already gone */ }
            _captureDevice = null;
        }
        _captureDeviceId = null;

        IsRunning = false;
        _processor.Reset();
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _shouldRun = false;

            _watchdog?.Dispose();
            _watchdog = null;

            try { _capture?.StopRecording(); } catch { /* ignore */ }
            DisposeCapture();

            _deviceEnumerator?.Dispose();
            _deviceEnumerator = null;
        }
    }
}
