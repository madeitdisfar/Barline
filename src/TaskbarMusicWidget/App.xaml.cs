using System.Windows;
using Microsoft.Win32;
using TaskbarMusicWidget.Audio;
using TaskbarMusicWidget.Diagnostics;
using TaskbarMusicWidget.Media;
using TaskbarMusicWidget.Shell;
using TaskbarMusicWidget.Startup;
using TaskbarMusicWidget.Tray;
using TaskbarMusicWidget.Ui;

namespace TaskbarMusicWidget;

public partial class App : Application
{
    /// <summary>
    /// Session-scoped so a second sign-in gets its own widget, while a duplicate
    /// launch in the same session is rejected. Two overlays would fight over the
    /// same strip of taskbar.
    /// </summary>
    private const string InstanceMutexName = @"Local\TaskbarMusicWidget.SingleInstance";

    private Mutex? _instanceMutex;
    private TaskbarTracker? _tracker;
    private MediaSessionService? _media;
    private LoopbackAnalyzer? _analyzer;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            DebugLog.Write("another instance is already running; exiting");
            Shutdown();
            return;
        }

        var tracker = new TaskbarTracker();
        var media = new MediaSessionService(Dispatcher);
        var theme = new Theme();
        var analyzer = new LoopbackAnalyzer();
        var window = new OverlayWindow(tracker, media, theme, analyzer);

        var autoStart = new AutoStartService();
        var tray = new TrayIcon(autoStart, window.VisualizerEnabled);

        tray.ExitRequested += (_, _) => Shutdown();
        tray.VisualizerToggled += (_, enabled) => window.VisualizerEnabled = enabled;
        tray.RestartVisualizerRequested += (_, _) => analyzer.Restart();
        window.ContextMenuRequested += (_, _) => tray.ShowContextMenu();

        // Resuming from sleep/hibernate is the classic case where the loopback
        // capture comes back dead; re-arm it proactively rather than waiting for
        // the watchdog to notice once playback resumes.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _tracker = tracker;
        _media = media;
        _analyzer = analyzer;
        _tray = tray;

        // Show first so the HWND exists and OnSourceInitialized can apply the
        // extended styles, then start tracking — the first Changed event places it.
        window.Show();
        tracker.Start();

        // Left running for the app's lifetime: WASAPI raises no callbacks during
        // silence, so an idle capture costs essentially nothing, and re-arming on
        // every play/pause would add latency to the first beat.
        analyzer.Start();

        if (DemoContent.Enabled)
        {
            // Demo mode stands in for SMTC entirely; otherwise the real session
            // would immediately publish "nothing playing" over the sample track.
            window.SetTrack(DemoContent.CreateTrack());
        }
        else
        {
            // SMTC negotiation is async and must not block startup; the widget
            // stays hidden until the first session resolves.
            _ = media.StartAsync();
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            DebugLog.Write("power resume; restarting loopback capture");
            _analyzer?.Restart();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _tray?.Dispose();
        _analyzer?.Dispose();
        _media?.Dispose();
        _tracker?.Dispose();

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* never owned */ }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
