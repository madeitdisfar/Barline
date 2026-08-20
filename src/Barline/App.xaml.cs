using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Barline.Audio;
using Barline.Diagnostics;
using Barline.Lyrics;
using Barline.Media;
using Barline.Platform;
using Barline.Settings;
using Barline.Shell;
using Barline.Startup;
using Barline.Tray;
using Barline.Ui;

namespace Barline;

public partial class App : Application
{
    /// <summary>
    /// Session-scoped so a second sign-in gets its own widget, while a duplicate
    /// launch in the same session is rejected. Two overlays would fight over the
    /// same strip of taskbar.
    /// </summary>
    private const string InstanceMutexName = @"Local\Barline.SingleInstance";

    /// <summary>
    /// How long a launch waits for a predecessor to let go before deciding it is a
    /// duplicate rather than a successor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A restart is two processes for a moment. The successor starts while the old one
    /// is still tearing down its capture, its tray icon and its media session, and
    /// without a wait it loses that race, refuses itself as a duplicate and exits. The
    /// restart then looks exactly like having quit, which is what it looked like.
    /// </para>
    /// <para>
    /// Waiting rather than refusing outright, because finding this taken usually means
    /// a restart is in progress rather than that somebody launched a second copy by
    /// hand. A genuine duplicate waits the timeout out and then exits, which nobody
    /// sees: it has drawn nothing by this point. Generous on purpose, since the cost of
    /// being too patient is an invisible background process and the cost of being too
    /// impatient is the bug this exists to fix.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(5);

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
        if (!isFirstInstance && !WaitForPredecessor(_instanceMutex))
        {
            DebugLog.Write("another instance is already running; exiting");

            // Closed rather than left to OnExit, which would try to release a lock this
            // process never acquired.
            _instanceMutex.Dispose();
            _instanceMutex = null;

            Shutdown();
            return;
        }

        var settings = new SettingsStore();

        // Answered from disk alone, so startup never waits on the Store. The Store
        // itself is asked once a window exists to own the call, below.
        var license = new LicenseService();

        ApplyLicense(settings, license);

        // Armed after the startup strip, so the guard never mistakes the values that
        // pass is about to remove for ones this session introduced.
        settings.Guard(() => license.Premium);

        var tracker = new TaskbarTracker { TargetDisplayId = settings.Current.DisplayId };
        var media = new MediaSessionService(Dispatcher);
        var theme = new Theme();
        var analyzer = new LoopbackAnalyzer();
        var lyrics = new LyricsService(settings, Dispatcher, license.Premium);
        var window = new OverlayWindow(tracker, media, theme, analyzer, settings, lyrics);

        window.VisualizerEnabled = settings.Current.VisualizerEnabled;

        var autoStart = new AutoStartService();
        var tray = new TrayIcon(settings.Current, theme);

        tray.ExitRequested += (_, _) => Shutdown();
        tray.RestartVisualizerRequested += (_, _) => analyzer.Restart();
        tray.RestartRequested += (_, _) => Restart();
        tray.SettingsRequested += (_, _) => ShowSettings(theme, settings, autoStart, window, media, lyrics, license);
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

            // Assigning the same id again does nothing, so this costs a comparison on
            // every other setting rather than an acquisition.
            tracker.TargetDisplayId = settings.Current.DisplayId;
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

        // Now that there is a handle to own it. Deliberately not awaited: the widget
        // must be on the taskbar whether or not the Store ever answers.
        _ = AskTheStore(window, settings, license);

        // Same pattern: shown so the handle exists, then it hides itself until there
        // is a lyric to put in it.
        var panel = new LyricsPanel(tracker, media, settings, lyrics);
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

        // Nothing is playing on a machine this has just been installed on, so the
        // widget is hidden and the taskbar looks untouched. Without this the app is
        // indistinguishable from one that failed to start.
        if (settings.IsFirstRun || DevOverride.IsSet("BARLINE_WELCOME"))
            new WelcomeWindow(theme, settings).Show();

        // Only reachable by buying the add-on, which cannot be done on demand, so it
        // gets the same escape hatch the welcome window has.
        if (DevOverride.IsSet("BARLINE_THANKS"))
            new ThankYouWindow(theme).Show();

        // Opened last, after a track exists: iterating on this window otherwise means
        // a tray right-click on every rebuild, and the tray menu is awkward to script.
        if (DevOverride.IsOn("BARLINE_SETTINGS"))
            ShowSettings(theme, settings, autoStart, window, media, lyrics, license);
    }

    /// <summary>
    /// Restarts the app from the tray menu.
    /// </summary>
    /// <remarks>
    /// A refusal shuts nothing down, so the app is still sitting there and the click
    /// looks like it did nothing at all. The tray has no status line to say so in, and
    /// the failure is worth more than a log entry to somebody who just asked for this,
    /// so it is said in the one surface that is always available.
    /// </remarks>
    private static void Restart()
    {
        if (AppRestart.TryRestart()) return;

        MessageBox.Show(
            "Barline could not restart itself. Close it from the notification area "
                + "and open it again.",
            AppInfo.Name,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Waits for a predecessor to release the single-instance lock.
    /// </summary>
    /// <returns>True when this process now holds it and may carry on.</returns>
    /// <remarks>
    /// Blocking the UI thread is safe here and nowhere later: this runs before any
    /// window exists, so there is nothing on screen to stop responding. See
    /// <see cref="HandoverTimeout"/> for why the wait is there at all.
    /// </remarks>
    private static bool WaitForPredecessor(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(HandoverTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The predecessor died without releasing it, which is what Windows
            // terminating the process for a restart looks like from here. The wait
            // still succeeded and this process now holds the lock.
            DebugLog.Write("predecessor exited without releasing the instance lock");
            return true;
        }
    }

    /// <summary>
    /// Brings the settings into line with what the license currently says.
    /// </summary>
    /// <remarks>
    /// Runs at startup and again if the Store later contradicts it, because both
    /// directions have to be handled: a first launch that turns out not to own the
    /// add-on strips, and one that turns out to own it puts back whatever an earlier
    /// run took away.
    /// </remarks>
    private static void ApplyLicense(SettingsStore settings, LicenseService license)
    {
        if (license.MayStrip) settings.UpdateIf(PremiumSettings.Strip);
        else if (license.Premium) settings.UpdateIf(PremiumSettings.Restore);
    }

    /// <summary>
    /// Asks the Store about the add-on, and acts on it only if the answer moved.
    /// </summary>
    /// <remarks>
    /// The presets are re-seeded as well as the settings, since the paid built-ins are
    /// skipped entirely by a free run and a launch that has just discovered it owns
    /// them is the moment they should appear.
    /// </remarks>
    private static async Task AskTheStore(
        Window window, SettingsStore settings, LicenseService license)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero) return;

        if (!await license.RefreshAsync(handle)) return;

        ApplyLicense(settings, license);

        new LyricsPresetStore().EnsureBuiltIns(license.Premium);
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
        OverlayWindow window,
        MediaSessionService media,
        LyricsService lyrics,
        LicenseService license)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(
                theme, settings, autoStart, window, media, lyrics, license);

            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            return;
        }

        // Already open: restore it if minimized and pull it to the front rather than
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
