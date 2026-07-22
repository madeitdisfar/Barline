using System.Windows;
using TaskbarMusicWidget.Diagnostics;
using TaskbarMusicWidget.Media;
using TaskbarMusicWidget.Shell;
using TaskbarMusicWidget.Ui;

namespace TaskbarMusicWidget;

public partial class App : Application
{
    private TaskbarTracker? _tracker;
    private MediaSessionService? _media;
    private Theme? _theme;
    private OverlayWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _tracker = new TaskbarTracker();
        _media = new MediaSessionService(Dispatcher);
        _theme = new Theme();
        _window = new OverlayWindow(_tracker, _media, _theme);

        // Show first so the HWND exists and OnSourceInitialized can apply the
        // extended styles, then start tracking — the first Changed event places it.
        _window.Show();
        _tracker.Start();

        if (DemoContent.Enabled)
        {
            // Demo mode stands in for SMTC entirely; otherwise the real session
            // would immediately publish "nothing playing" over the sample track.
            _window.SetTrack(DemoContent.CreateTrack());
        }
        else
        {
            // SMTC negotiation is async and must not block startup; the widget
            // stays hidden until the first session resolves.
            _ = _media.StartAsync();
        }

        // TEMPORARY (Phase 1-3): right-click to exit. Replaced by the tray menu
        // in Phase 6. Without this there is no way to quit during testing.
        _window.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            Shutdown();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _media?.Dispose();
        _tracker?.Dispose();
        base.OnExit(e);
    }
}
