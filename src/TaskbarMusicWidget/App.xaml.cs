using System.Windows;
using Microsoft.Win32;
using TaskbarMusicWidget.Audio;
using TaskbarMusicWidget.Diagnostics;
using TaskbarMusicWidget.Lyrics;
using TaskbarMusicWidget.Media;
using TaskbarMusicWidget.Settings;
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
    private SettingsWindow? _settingsWindow;

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

        var settings = new SettingsStore();
        var tracker = new TaskbarTracker();
        var media = new MediaSessionService(Dispatcher);
        var theme = new Theme();
        var analyzer = new LoopbackAnalyzer();
        var lyrics = new LyricsService(settings, Dispatcher);
        var window = new OverlayWindow(tracker, media, theme, analyzer, settings, lyrics);

        window.VisualizerEnabled = settings.Current.VisualizerEnabled;

        var autoStart = new AutoStartService();
        var tray = new TrayIcon(settings.Current);

        tray.ExitRequested += (_, _) => Shutdown();
        tray.RestartVisualizerRequested += (_, _) => analyzer.Restart();
        tray.SettingsRequested += (_, _) => ShowSettings(theme, settings, autoStart, window);
        window.ContextMenuRequested += (_, _) => tray.ShowContextMenu();

        tray.VisualizerToggled += (_, enabled) =>
        {
            window.VisualizerEnabled = enabled;
            settings.Update(s => s.VisualizerEnabled = enabled);
        };

        // The settings window writes the same setting, so the menu's checkmark is
        // pushed back from the store rather than only being set at construction.
        settings.Changed += (_, _) =>
        {
            window.VisualizerEnabled = settings.Current.VisualizerEnabled;
            tray.SetVisualizerChecked(settings.Current.VisualizerEnabled);
        };

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

        // Same pattern: shown so the handle exists, then it hides itself until there
        // is a lyric to put in it.
        var panel = new LyricsPanel(tracker, media, theme, settings, lyrics);
        panel.Show();
        window.AttachPanel(panel);

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

        // Opened last, after a track exists: iterating on this window otherwise means
        // a tray right-click on every rebuild, and the tray menu is awkward to script.
        if (Environment.GetEnvironmentVariable("TMW_SETTINGS") == "1")
            ShowSettings(theme, settings, autoStart, window);
    }

    /// <summary>
    /// Opens the settings window, or brings the existing one forward.
    /// </summary>
    /// <remarks>
    /// Created lazily and kept for the app's lifetime: a widget that mostly runs
    /// untouched should not pay for a window nobody opens, and reopening a closed one
    /// is cheap enough that caching a hidden instance would only add state to get
    /// wrong.
    /// </remarks>
    private void ShowSettings(
        Ui.Theme theme,
        SettingsStore settings,
        AutoStartService autoStart,
        OverlayWindow window)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(theme, settings, autoStart, window);

            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            return;
        }

        // Already open: restore it if minimised and pull it to the front rather than
        // opening a second copy.
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;

        _settingsWindow.Activate();
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
