using System.Windows.Threading;
using TaskbarMusicWidget.Diagnostics;
using Windows.Foundation;
using Windows.Media.Control;

namespace TaskbarMusicWidget.Media;

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
/// Every WinRT event arrives on a thread-pool thread. All publishing is marshalled
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
        DebugLog.Write($"attached session: {_session.SourceAppUserModelId}");
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session = null;
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args)
    {
        if (!_disposed) _ = RefreshAsync();
    }

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args)
    {
        if (!_disposed) _ = RefreshAsync();
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
