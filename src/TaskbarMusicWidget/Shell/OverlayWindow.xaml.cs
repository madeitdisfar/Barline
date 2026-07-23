using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using TaskbarMusicWidget.Audio;
using TaskbarMusicWidget.Diagnostics;
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

    // Layout constants, mirroring the XAML, used to derive the width available to
    // the title/artist lines. They must stay in sync with OverlayWindow.xaml:
    // Root Margin, the album-art width, the text block's left margin and the
    // right-zone width.
    private const double RootMarginLeft = 8d;
    private const double RootMarginRight = 12d;
    private const double ArtWidth = 32d;
    private const double TextMarginLeft = 10d;
    private const double RightZoneWidth = 88d;

    /// <summary>
    /// Logical width available to the title/artist text. Used as an explicit
    /// <c>MaxWidth</c> on the lines: a NoWrap TextBlock in a star column otherwise
    /// expands to its full content width and is merely clipped, so its ActualWidth
    /// never reflects the visible width and the overflow fade cannot be placed.
    /// </summary>
    private const double AvailableTextWidth =
        WidgetLogicalWidth - RootMarginLeft - ArtWidth - TextMarginLeft - RightZoneWidth - RootMarginRight;

    private const int HoverFadeMs = 150;

    /// <summary>The Fluent "standard" easing curve, cubic-bezier(0.33, 0, 0.67, 1).</summary>
    private static readonly KeySpline FluentStandard = CreateFluentSpline();

    private Brush? _edgeFade;

    private readonly TaskbarTracker _tracker;
    private readonly MediaSessionService _media;
    private readonly Theme _theme;
    private readonly LoopbackAnalyzer _analyzer;

    /// <summary>
    /// Delays hiding so brief taskbar transitions don't flash the widget.
    /// </summary>
    /// <remarks>
    /// Explorer restarts and auto-hide state changes momentarily report the
    /// taskbar as unavailable — measured at roughly 17ms — and track changes can
    /// publish a null in the gap between songs. Hiding immediately turns each of
    /// those into a visible blink. Showing is never delayed.
    /// </remarks>
    private readonly DispatcherTimer _hideDebounce;

    private uint _taskbarCreatedMessage;
    private IntPtr _hwnd;
    private IntPtr _ownerHandle;
    private TrackInfo? _track;
    private bool _hovered;
    private bool _visualizerEnabled = true;

    /// <summary>Raised on right-click, so the host can show the tray menu.</summary>
    public event EventHandler? ContextMenuRequested;

    public OverlayWindow(
        TaskbarTracker tracker,
        MediaSessionService media,
        Theme theme,
        LoopbackAnalyzer analyzer)
    {
        _tracker = tracker;
        _media = media;
        _theme = theme;
        _analyzer = analyzer;

        InitializeComponent();

        _hideDebounce = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _hideDebounce.Tick += (_, _) =>
        {
            _hideDebounce.Stop();
            HideNow();
        };

        // Pull-based: the visualiser samples the latest spectrum once per frame.
        // Returning false (no capture, or silence) drops it back to decorative motion.
        Bars.LevelSource = levels => _analyzer.TryGetLevels(levels);

        PreviousButton.Click += (_, _) => _ = _media.SkipPreviousAsync();
        PlayPauseButton.Click += (_, _) => _ = _media.TogglePlayPauseAsync();
        NextButton.Click += (_, _) => _ = _media.SkipNextAsync();

        _tracker.Changed += (_, state) => Apply(state);
        _media.TrackChanged += (_, track) => SetTrack(track);
        _theme.Changed += (_, _) => ApplyTheme();

        // Fix the text area to the real available width so its clip and fade mask
        // map to the visible region (the lines inside render full-width and clip).
        TextArea.Width = AvailableTextWidth;

        // Recompute the overflow fade when a line's text changes; SetTrack also
        // triggers it. SizeChanged covers first layout and DPI changes.
        TitleText.SizeChanged += (_, _) => UpdateTextFades();
        ArtistText.SizeChanged += (_, _) => UpdateTextFades();

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

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;

        // Right-clicking the widget opens the same menu as the tray icon — the
        // widget has no other chrome to hang settings off.
        ContextMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hides the bars while leaving hover controls intact, for users who want the
    /// widget purely informational.
    /// </summary>
    public bool VisualizerEnabled
    {
        get => _visualizerEnabled;
        set
        {
            if (_visualizerEnabled == value) return;
            _visualizerEnabled = value;
            Bars.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
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

        // ActualWidth is only valid after the next layout pass, so defer.
        Dispatcher.BeginInvoke(new Action(UpdateTextFades), DispatcherPriority.Loaded);

        bool playing = track?.IsPlaying == true;
        Bars.IsActive = playing;
        // Lets the capture watchdog distinguish a real stall from ordinary silence.
        _analyzer.ExpectingAudio = playing;
        PlayPauseGlyph.Data = (Geometry)FindResource(playing ? "PauseGeometry" : "PlayGeometry");

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

    /// <summary>
    /// Fades the right edge of the text area, but only when the title or artist
    /// actually overflows the available width. A line that fits never reaches the
    /// fade band, so this leaves short and medium titles untouched and softens only
    /// the ones that are genuinely clipped.
    /// </summary>
    private void UpdateTextFades()
    {
        _edgeFade ??= (Brush)FindResource("EdgeFade");

        bool overflows =
            MeasureTextWidth(TitleText) > AvailableTextWidth + 1d ||
            MeasureTextWidth(ArtistText) > AvailableTextWidth + 1d;

        TextArea.OpacityMask = overflows ? _edgeFade : null;
    }

    private static double MeasureTextWidth(TextBlock line)
    {
        var typeface = new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(line).PixelsPerDip;

        var formatted = new FormattedText(
            line.Text,
            CultureInfo.CurrentUICulture,
            line.FlowDirection,
            typeface,
            line.FontSize,
            Brushes.Black,
            pixelsPerDip);

        return formatted.WidthIncludingTrailingWhitespace;
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
            // Deferred — a transient unavailable state should not blink the widget.
            if (!_hideDebounce.IsEnabled) _hideDebounce.Start();
            return;
        }

        // Showing is immediate; cancel any pending hide.
        _hideDebounce.Stop();

        EnsureTaskbarOwnership();

        double scale = state.Dpi / 96d;

        int width = (int)Math.Round(WidgetLogicalWidth * scale);
        int height = state.Rect.Height;
        int x = state.Rect.Left + (int)Math.Round(LeftInsetLogical * scale);
        int y = state.Rect.Top;

        // Position in physical pixels and re-assert topmost in the same call.
        // Re-asserting on every taskbar change keeps the widget above other
        // topmost windows; ownership (below) keeps it above the taskbar itself.
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Makes the taskbar the widget's owner window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-asserting <c>HWND_TOPMOST</c> is not enough on its own. Clicking the
    /// taskbar — an icon or empty space — raises <c>Shell_TrayWnd</c> within the
    /// topmost band and fires no foreground or location event, so there is nothing
    /// to react to and the widget ends up behind the taskbar.
    /// </para>
    /// <para>
    /// The window manager guarantees an owned window stays above its owner in
    /// z-order. Owning the widget to the taskbar makes it ride up automatically
    /// whenever the taskbar raises itself, with no event handling at all.
    /// </para>
    /// <para>
    /// The handle changes when Explorer restarts, so this re-owns to the current
    /// one. A cross-process owner cannot destroy our window (that only happens
    /// within the owner's own thread), so a stale handle briefly during a restart
    /// is harmless.
    /// </para>
    /// </remarks>
    private void EnsureTaskbarOwnership()
    {
        IntPtr taskbar = _tracker.TaskbarHandle;
        if (taskbar == IntPtr.Zero || taskbar == _ownerHandle) return;

        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, taskbar);
        _ownerHandle = taskbar;
        DebugLog.Write($"owner set to taskbar 0x{taskbar:X}");
    }

    private void HideNow()
    {
        if (_hwnd == IntPtr.Zero) return;

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
    }
}
