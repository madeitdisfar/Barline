using System.Windows.Threading;
using Barline.Diagnostics;
using Windows.Foundation;
using Windows.Media.Control;

namespace Barline.Media;

using PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = GlobalSystemMediaTransportControlsSession;
using SessionManager = GlobalSystemMediaTransportControlsSessionManager;

/// <summary>
/// Wraps the System Media Transport Controls (SMTC) and publishes a flattened
/// <see cref="TrackInfo"/> whenever what's playing changes.
/// </summary>
/// <remarks>
/// <para>
/// SMTC is the same source the Windows volume flyout uses, so anything that shows
/// up there — Spotify, Apple Music, browsers, podcast apps — works here for free.
/// </para>
/// <para>
/// Every WinRT event arrives on a thread-pool thread. All publishing is marshaled
/// to the UI dispatcher so consumers never have to think about it.
/// </para>
/// </remarks>
internal sealed class MediaSessionService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AlbumArtCache _artCache = new();

    private SessionManager? _manager;
    private Session? _session;

    /// <summary>
    /// Guards against a slow refresh publishing over a newer one. Refreshes are
    /// async and can overlap when several change events land together.
    /// </summary>
    private int _refreshToken;

    private bool _disposed;

    public TrackInfo? Current { get; private set; }

    /// <summary>
    /// Continuous playback position, extrapolated from the session's occasional
    /// reports. Safe to poll per frame — it is arithmetic, not a query.
    /// </summary>
    public PlaybackClock Clock { get; } = new();

    /// <summary>Raised on the UI thread. A null payload means nothing is playing.</summary>
    public event EventHandler<TrackInfo?>? TrackChanged;

    public MediaSessionService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task StartAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachSession(_manager.GetCurrentSession());
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // SMTC can be unavailable in constrained environments. The widget
            // should stay dormant rather than crash.
            DebugLog.Write($"SMTC unavailable: {ex.Message}");
            Publish(null);
        }
    }

    // ---- Session wiring ---------------------------------------------------

    private void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (_disposed) return;
        AttachSession(sender.GetCurrentSession());
        _ = RefreshAsync();
    }

    private void AttachSession(Session? session)
    {
        if (ReferenceEquals(session, _session)) return;

        DetachSession();

        _session = session;
        if (_session is null)
        {
            DebugLog.Write("no active media session");
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        DebugLog.Write($"attached session: {_session.SourceAppUserModelId}");

        // Arm the clock straight away. Waiting for the first event would leave it
        // blind for however long the source app takes to publish one, which for a
        // paused or steadily playing session can be a long time.
        AnchorClock(_session);
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;

        Clock.Reset();
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args)
    {
        if (!_disposed) _ = RefreshAsync();
    }

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args)
    {
        if (_disposed) return;

        // Play, pause and seek all land here, and each of them invalidates the
        // extrapolation, so re-anchor before the slower refresh runs.
        AnchorClock(sender);
        _ = RefreshAsync();
    }

    /// <summary>
    /// Position reports arrive far more often than anything else changes, so this
    /// deliberately does not run a full refresh — that would re-query metadata and
    /// re-decode album art several times a minute for a number that fits in a long.
    /// </summary>
    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args)
    {
        if (!_disposed) AnchorClock(sender);
    }

    private void AnchorClock(Session session)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();
            if (timeline is null || playback is null) return;

            var receivedAt = DateTimeOffset.UtcNow;

            var anchor = new PlaybackAnchor(
                Position: timeline.Position,
                // Not always zero: some sources describe a window into a longer
                // stream, so the length is the span rather than the end.
                Duration: timeline.EndTime - timeline.StartTime,
                Rate: playback.PlaybackRate ?? 1d,
                IsPlaying: playback.PlaybackStatus == PlaybackStatus.Playing,
                ReportedAt: timeline.LastUpdatedTime);

            // How stale the source says its own report is. Spotify keeps this near
            // zero; a source that never advances it is not maintaining the timestamp
            // at all, which makes the error figure below meaningless rather than good.
            double age = (receivedAt - timeline.LastUpdatedTime).TotalSeconds;

            if (!Clock.Anchor(anchor, receivedAt))
            {
                DebugLog.Write(
                    $"clock: rejected report pos={timeline.Position} " +
                    $"len={anchor.Duration} at={timeline.LastUpdatedTime:O}");
                return;
            }

            // Every accepted report is logged, not only the ones with an error to
            // show, so the log distinguishes "the clock is accurate" from "the clock
            // is never being told anything".
            string error = Clock.LastPredictionError is { } e
                ? $"{e.TotalMilliseconds,7:F0}ms"
                : "      --";

            DebugLog.Write(
                $"clock: error={error} age={age,6:F2}s " +
                $"pos={anchor.Position:mm\\:ss\\.fff} " +
                $"len={anchor.Duration:mm\\:ss} " +
                $"rate={anchor.Rate:F2} playing={anchor.IsPlaying}");
        }
        catch (Exception ex)
        {
            // The session can die between the event firing and this query.
            DebugLog.Write($"clock: anchor failed: {ex.Message}");
        }
    }

    // ---- Refresh ----------------------------------------------------------

    private async Task RefreshAsync()
    {
        int token = Interlocked.Increment(ref _refreshToken);

        var session = _session;
        if (session is null)
        {
            Publish(null, token);
            return;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            if (properties is null || playback is null)
            {
                Publish(null, token);
                return;
            }

            var art = await _artCache.GetAsync(properties);

            // Some sources leave Artist empty and populate AlbumArtist instead.
            string artist = string.IsNullOrWhiteSpace(properties.Artist)
                ? properties.AlbumArtist ?? string.Empty
                : properties.Artist;

            var controls = playback.Controls;

            Publish(new TrackInfo
            {
                Title = properties.Title ?? string.Empty,
                Artist = artist,
                AlbumTitle = properties.AlbumTitle ?? string.Empty,
                IsPlaying = playback.PlaybackStatus == PlaybackStatus.Playing,
                AlbumArt = art,
                CanGoNext = controls.IsNextEnabled,
                CanGoPrevious = controls.IsPreviousEnabled,
                CanPlayPause = controls.IsPlayEnabled || controls.IsPauseEnabled,
                SourceAppId = session.SourceAppUserModelId,
            }, token);
        }
        catch (Exception ex)
        {
            // Sessions die mid-query when an app closes; that is expected.
            DebugLog.Write($"media refresh failed: {ex.Message}");
        }
    }

    private void Publish(TrackInfo? track, int token = -1)
    {
        // Drop results that a newer refresh has already superseded.
        if (token != -1 && token != Volatile.Read(ref _refreshToken)) return;

        if (_dispatcher.CheckAccess())
        {
            Apply(track);
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() => Apply(track)));
        }
    }

    private void Apply(TrackInfo? track)
    {
        if (_disposed) return;
        Current = track;
        DebugLog.Write(track is null
            ? "track: <none>"
            : $"track: '{track.Title}' — '{track.Artist}' playing={track.IsPlaying} art={(track.AlbumArt is not null)}");
        TrackChanged?.Invoke(this, track);
    }

    // ---- Transport commands ----------------------------------------------

    public Task TogglePlayPauseAsync() => Invoke(s => s.TryTogglePlayPauseAsync());
    public Task SkipNextAsync() => Invoke(s => s.TrySkipNextAsync());
    public Task SkipPreviousAsync() => Invoke(s => s.TrySkipPreviousAsync());

    private async Task Invoke(Func<Session, IAsyncOperation<bool>> command)
    {
        var session = _session;
        if (session is null) return;

        try
        {
            await command(session);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"transport command failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachSession();
        if (_manager is not null)
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
    }
}
