using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using TaskbarMusicWidget.Audio;
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

    private const int HoverFadeMs = 150;

    // Segoe Fluent Icons — Windows 11's own icon font. Using the system glyphs
    // rather than custom paths is a large part of why this reads as native.
    private const string GlyphPlay = "";
    private const string GlyphPause = "";

    /// <summary>The Fluent "standard" easing curve, cubic-bezier(0.33, 0, 0.67, 1).</summary>
    private static readonly KeySpline FluentStandard = CreateFluentSpline();

    private readonly TaskbarTracker _tracker;
    private readonly MediaSessionService _media;
    private readonly Theme _theme;

    private uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private TrackInfo? _track;
    private bool _hovered;

    public OverlayWindow(
        TaskbarTracker tracker,
        MediaSessionService media,
        Theme theme,
        LoopbackAnalyzer analyzer)
    {
        _tracker = tracker;
        _media = media;
        _theme = theme;

        InitializeComponent();

        // Pull-based: the visualiser samples the latest spectrum once per frame.
        // Returning false (no capture, or silence) drops it back to decorative motion.
        Bars.LevelSource = levels => analyzer.TryGetLevels(levels);

        PreviousButton.Click += (_, _) => _ = _media.SkipPreviousAsync();
        PlayPauseButton.Click += (_, _) => _ = _media.TogglePlayPauseAsync();
        NextButton.Click += (_, _) => _ = _media.SkipNextAsync();

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

    // ---- Hover -------------------------------------------------------------

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetHovered(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(false);
    }

    /// <summary>
    /// Crossfades the visualiser and the transport controls in place. Both live in
    /// the same fixed-width zone, so nothing moves — only opacity changes.
    /// </summary>
    private void SetHovered(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;

        TransportPanel.IsHitTestVisible = hovered;
        Fade(TransportPanel, hovered ? 1d : 0d);
        Fade(Bars, hovered ? 0d : 1d);
    }

    private static void Fade(UIElement element, double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(HoverFadeMs)),
            FluentStandard));

        element.BeginAnimation(OpacityProperty, animation);
    }

    private static KeySpline CreateFluentSpline()
    {
        var spline = new KeySpline(0.33, 0.0, 0.67, 1.0);
        spline.Freeze();
        return spline;
    }

    // ---- Click-to-focus ----------------------------------------------------

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // Transport buttons handle their own clicks; only the art/text area
        // should bring the player forward.
        if (IsWithinTransport(e.OriginalSource as DependencyObject))
            return;

        SourceAppActivator.TryActivate(_track?.SourceAppId);
    }

    private bool IsWithinTransport(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, TransportPanel)) return true;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null;
        }
        return false;
    }

    // ---- Content -----------------------------------------------------------

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

        bool playing = track?.IsPlaying == true;
        Bars.IsActive = playing;
        PlayPauseButton.Content = playing ? GlyphPause : GlyphPlay;

        // Sources advertise different capabilities — a podcast app may offer no
        // "previous", a radio stream neither. Drive the buttons from what the
        // session actually reports rather than assuming.
        PreviousButton.IsEnabled = track?.CanGoPrevious == true;
        NextButton.IsEnabled = track?.CanGoNext == true;
        PlayPauseButton.IsEnabled = track?.CanPlayPause == true;

        // Nothing playing means nothing to show. Hiding entirely is better than
        // an empty shell, and it gives the taskbar its space back.
        Apply(_tracker.Current);
    }

    private void ApplyTheme()
    {
        // Swapping the resources updates everything bound with DynamicResource,
        // including the button template's hover and pressed fills.
        Resources["TextPrimaryBrush"] = _theme.TextPrimary;
        Resources["TextSecondaryBrush"] = _theme.TextSecondary;
        Resources["TextTertiaryBrush"] = _theme.TextTertiary;
        Resources["SubtleHoverBrush"] = _theme.SubtleHover;
        Resources["SubtlePressedBrush"] = _theme.SubtlePressed;
        Resources["ArtPlaceholderBrush"] = _theme.ArtPlaceholder;

        Bars.BarBrush = _theme.TextPrimary;
    }

    // ---- Placement ---------------------------------------------------------

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
