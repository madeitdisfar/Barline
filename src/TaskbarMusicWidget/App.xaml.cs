using System.Windows;
using System.Windows.Input;
using TaskbarMusicWidget.Shell;

namespace TaskbarMusicWidget;

public partial class App : Application
{
    private TaskbarTracker? _tracker;
    private OverlayWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _tracker = new TaskbarTracker();
        _window = new OverlayWindow(_tracker);

        // Show first so the HWND exists and OnSourceInitialized can apply the
        // extended styles, then start tracking — the first Changed event places it.
        _window.Show();
        _tracker.Start();

        // TEMPORARY (Phase 1): right-click to exit. Replaced by the tray menu
        // in Phase 6. Without this there is no way to quit during testing.
        _window.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            Shutdown();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tracker?.Dispose();
        base.OnExit(e);
    }
}
