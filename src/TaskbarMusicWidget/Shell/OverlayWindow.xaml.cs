using System.Windows;
using System.Windows.Interop;
using TaskbarMusicWidget.Media;
using TaskbarMusicWidget.Ui;
using static TaskbarMusicWidget.Shell.NativeMethods;

namespace TaskbarMusicWidget.Shell;

/// <summary>
/// The widget's host window: a transparent, non-activating overlay pinned to the
/// left end of the taskbar.
/// <para>
/// It paints no background of its own. The real taskbar's Mica / acrylic material
/// shows through, so the widget inherits the system backdrop exactly and stays
/// correct across theme, accent and transparency changes without us reproducing
/// any of it.
/// </para>
/// </summary>
internal partial class OverlayWindow : Window
{
    /// <summary>Widget width in logical (DPI-independent) pixels.</summary>
    private const double WidgetLogicalWidth = 300d;

    /// <summary>Gap between the taskbar's left edge and the widget, in logical pixels.</summary>
    private const double LeftInsetLogical = 0d;

    private readonly TaskbarTracker _tracker;
    private readonly MediaSessionService _media;
    private readonly Theme _theme;

    private uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private TrackInfo? _track;

    public OverlayWindow(TaskbarTracker tracker, MediaSessionService media, Theme theme)
    {
        _tracker = tracker;
        _media = media;
        _theme = theme;

        InitializeComponent();

        _tracker.Changed += (_, state) => Apply(state);
        _media.TrackChanged += (_, track) => SetTrack(track);
        _theme.Changed += (_, _) => ApplyTheme();

        ApplyTheme();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_NOACTIVATE  - clicking the widget never steals focus from the
        //                     user's active app.
        // WS_EX_TOOLWINDOW  - keeps the widget out of Alt+Tab.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

        // Explorer broadcasts this when it restarts; it is the signal to
        // re-acquire the taskbar handle.
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        Apply(_tracker.Current);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            _tracker.HandleTaskbarRecreated();
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            // Theme and accent changes arrive here (as ImmersiveColorSet).
            _theme.Refresh();
        }
        else if (msg is WM_DPICHANGED or WM_DISPLAYCHANGE)
        {
            // Let WPF finish its own DPI bookkeeping first, then re-place.
            Dispatcher.BeginInvoke(new Action(() => Apply(_tracker.Current)));
        }

        return IntPtr.Zero;
    }

    // ---- Content ----------------------------------------------------------

    /// <summary>Applies a track to the view. Also the injection point for demo mode.</summary>
    internal void SetTrack(TrackInfo? track)
    {
        _track = track;

        bool hasArt = track?.AlbumArt is not null;
        ArtBrush.ImageSource = track?.AlbumArt;
        ArtLayer.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        ArtFallbackGlyph.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;

        TitleText.Text = track?.Title ?? string.Empty;
        ArtistText.Text = track?.Artist ?? string.Empty;

        Bars.IsActive = track?.IsPlaying == true;

        // Nothing playing means nothing to show. Hiding entirely is better than
        // an empty shell, and it gives the taskbar its space back.
        Apply(_tracker.Current);
    }

    private void ApplyTheme()
    {
        TitleText.Foreground = _theme.TextPrimary;
        ArtistText.Foreground = _theme.TextSecondary;
        ArtFallbackGlyph.Foreground = _theme.TextTertiary;
        ArtPlaceholder.Background = _theme.ArtPlaceholder;
        Bars.BarBrush = _theme.TextPrimary;
    }

    // ---- Placement --------------------------------------------------------

    private void Apply(TaskbarState state)
    {
        if (_hwnd == IntPtr.Zero) return;

        bool hasContent = _track?.HasContent == true;

        if (!state.IsAvailable || !state.ShouldShow || !hasContent)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            return;
        }

        double scale = state.Dpi / 96d;

        int width = (int)Math.Round(WidgetLogicalWidth * scale);
        int height = state.Rect.Height;
        int x = state.Rect.Left + (int)Math.Round(LeftInsetLogical * scale);
        int y = state.Rect.Top;

        // Position in physical pixels and re-assert topmost in the same call.
        // Re-asserting on every taskbar change is what keeps the widget from
        // being buried when other topmost windows come and go.
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
