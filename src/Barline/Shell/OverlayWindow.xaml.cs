using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Barline.Audio;
using Barline.Diagnostics;
using Barline.Lyrics;
using Barline.Media;
using Barline.Settings;
using Barline.Ui;
using static Barline.Shell.NativeMethods;

namespace Barline.Shell;

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
internal partial class OverlayWindow : Window, IAlbumArtSource
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
    /// Logical width available to the title/artist text while the right zone is in
    /// use. Applied as an explicit width on the container: a NoWrap TextBlock in a star
    /// column otherwise expands to its full content width and is merely clipped, so its
    /// ActualWidth never reflects the visible width and the overflow fade cannot be
    /// placed.
    /// </summary>
    private const double AvailableTextWidth =
        WidgetLogicalWidth - RootMarginLeft - ArtWidth - TextMarginLeft - RightZoneWidth - RootMarginRight;

    /// <summary>
    /// The same, for when the right zone is empty and the text can have it.
    /// </summary>
    /// <remarks>
    /// 150px is around twenty-five characters, which is the tightest thing about the
    /// inline lyric. With the bars switched off the zone beside it is drawing nothing at
    /// all, so the text takes the room — over half as much again — rather than fading
    /// out in front of empty space.
    /// </remarks>
    private const double WideTextWidth = AvailableTextWidth + RightZoneWidth;

    /// <summary>The width the text area is currently laid out to, animation aside.</summary>
    private double _textWidth = AvailableTextWidth;

    private Brush? _edgeFade;

    private readonly TaskbarTracker _tracker;
    private readonly MediaSessionService _media;
    private readonly Theme _theme;
    private readonly LoopbackAnalyzer _analyzer;
    private readonly SettingsStore _settings;
    private readonly BarColorResolver _barColor;
    private readonly LyricsService _lyrics;
    private readonly DispatcherTimer _lyricPoll;

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

    /// <summary>
    /// Art for the track on display, or null when nothing is playing. Lets the
    /// settings window preview the album-art colour mode against the same cover the
    /// widget is currently showing.
    /// </summary>
    public ImageSource? CurrentAlbumArt => _track?.AlbumArt;

    public event EventHandler? AlbumArtChanged;

    public OverlayWindow(
        TaskbarTracker tracker,
        MediaSessionService media,
        Theme theme,
        LoopbackAnalyzer analyzer,
        SettingsStore settings,
        LyricsService lyrics)
    {
        _tracker = tracker;
        _media = media;
        _theme = theme;
        _analyzer = analyzer;
        _settings = settings;
        _lyrics = lyrics;
        _barColor = new BarColorResolver(theme, settings);

        InitializeComponent();

        _lyricPoll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = LyricPollInterval,
        };
        _lyricPoll.Tick += (_, _) => UpdateLyricLine();

        _lyrics.Changed += OnLyricsChanged;

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

        // Assigned once. The resolver animates this brush's colour in place, so the
        // visualiser never sees its colour change and needs no notification.
        Bars.BarBrush = _barColor.Brush;

        ApplyBarCount();

        PreviousButton.Click += (_, _) => _ = _media.SkipPreviousAsync();
        PlayPauseButton.Click += (_, _) => _ = _media.TogglePlayPauseAsync();
        NextButton.Click += (_, _) => _ = _media.SkipNextAsync();

        _tracker.Changed += (_, state) => Apply(state);
        _media.TrackChanged += (_, track) => SetTrack(track);
        _theme.Changed += (_, _) => ApplyTheme();
        _settings.Changed += (_, _) =>
        {
            ApplyBarColor();
            ApplyBarCount();

            // Turning lyrics on mid-track has to start a lookup that would otherwise
            // not happen until the next song, and turning them off has to take the
            // panel down — which the poll cannot do, because switching off is exactly
            // what stops the poll. Both are handled here, before polling is reassessed.
            _lyrics.SetTrack(_track, _media.Clock.Duration);
            ApplyLyricAppearance();
            UpdateLyricLine();
            UpdateLyricPolling();
        };

        // Fix the text area to the real available width so its clip and fade mask
        // map to the visible region (the lines inside render full-width and clip).
        ApplyTextWidth(animate: false);

        // Recompute the overflow fade when a line's text changes; SetTrack also
        // triggers it. SizeChanged covers first layout and DPI changes.
        TitleText.SizeChanged += (_, _) => UpdateTextFades();
        ArtistText.SizeChanged += (_, _) => UpdateTextFades();

        ApplyTheme();
        ApplyLyricAppearance();
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

        // The transport controls are about to occupy the right zone, so the text gives
        // it back for as long as they are there.
        ApplyTextWidth(animate: true);

        // Hovering is how you ask what is playing, so the title comes back while the
        // pointer is over the widget and the lyric steps aside.
        UpdateTextLayer();
    }

    /// <summary>
    /// Whether the lyric, rather than the title and artist, is currently shown.
    /// </summary>
    /// <remarks>
    /// Tracked so this can be called on every lyric poll — ten times a second — without
    /// starting a fresh animation each time to say what is already true.
    /// </remarks>
    private bool? _lyricShown;

    /// <summary>
    /// Crossfades between the lyric line and the title/artist pair.
    /// </summary>
    /// <remarks>
    /// The lyric only takes the space when there is genuinely something to show. A
    /// track with no lyrics, an instrumental passage between lines, lyrics moved out to
    /// the panel, or a paused widget all fall back to the title rather than leaving a
    /// line of a song nobody is playing sitting there.
    /// </remarks>
    private void UpdateTextLayer()
    {
        bool showLyrics = !_hovered && _track?.IsPlaying == true && _lyricLine.Length > 0;

        if (_lyricShown == showLyrics) return;
        _lyricShown = showLyrics;

        Fade(LyricsLayer, showLyrics ? 1d : 0d);
        Fade(TitleText, showLyrics ? 0d : 1d);
        Fade(ArtistText, showLyrics ? 0d : 1d);
    }

    /// <summary>
    /// Sizes the text area to whatever the right zone is not using.
    /// </summary>
    /// <remarks>
    /// Animated rather than snapped, because the transport controls it yields to are
    /// themselves fading in over the same 150ms — a hard resize under a soft crossfade
    /// reads as a glitch.
    /// </remarks>
    private void ApplyTextWidth(bool animate)
    {
        // The transport controls always win: they are drawn in that space, and text
        // running under them would be unreadable and look broken.
        double target = !_visualizerEnabled && !_hovered ? WideTextWidth : AvailableTextWidth;

        if (animate && Math.Abs(target - _textWidth) < 0.5d) return;
        _textWidth = target;

        if (animate)
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(
                target,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.FastMs)),
                Motion.Standard));

            TextArea.BeginAnimation(WidthProperty, animation);
        }
        else
        {
            TextArea.BeginAnimation(WidthProperty, null);
            TextArea.Width = target;
        }

        UpdateTextFades();
    }

    private static void Fade(UIElement element, double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.FastMs)),
            Motion.Standard));

        element.BeginAnimation(OpacityProperty, animation);
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

            // The zone the bars were in is now empty, and the text is the thing in this
            // layout that is genuinely short of room.
            ApplyTextWidth(animate: true);
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
        // Compared before the assignment: metadata ticks arrive far more often than
        // the track changes, and re-announcing the same cover would make the settings
        // window re-extract its palette several times a second.
        bool artChanged = !ReferenceEquals(_track?.AlbumArt, track?.AlbumArt);

        _track = track;

        bool hasArt = track?.AlbumArt is not null;
        ArtBrush.ImageSource = track?.AlbumArt;
        ArtLayer.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        ArtFallbackGlyph.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;

        // In album-art mode the bar colour is part of the track's identity, so it is
        // resolved here rather than only on theme changes.
        ApplyBarColor();

        TitleText.Text = track?.Title ?? string.Empty;
        ArtistText.Text = track?.Artist ?? string.Empty;

        // ActualWidth is only valid after the next layout pass, so defer.
        Dispatcher.BeginInvoke(new Action(UpdateTextFades), DispatcherPriority.Loaded);

        // The clock is anchored by the session service before this runs, so the
        // duration is already the new track's.
        _lyrics.SetTrack(track, _media.Clock.Duration);
        UpdateLyricLine();
        UpdateLyricPolling();

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

        if (artChanged)
            AlbumArtChanged?.Invoke(this, EventArgs.Empty);
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
            MeasureTextWidth(TitleText) > _textWidth + 1d ||
            MeasureTextWidth(ArtistText) > _textWidth + 1d ||
            MeasureTextWidth(LyricsText) > _textWidth + 1d;

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

        // Every colour mode is theme-dependent: the default token switches outright,
        // and the corrected modes are measured against the new taskbar material.
        ApplyBarColor();
    }

    // ---- Lyrics ------------------------------------------------------------

    /// <summary>
    /// Ten times a second. Lines change every few seconds, so a render-rate loop
    /// would be spending frames to re-decide the same answer; the word-level sweep
    /// is what will need one, and it does not exist yet.
    /// </summary>
    private static readonly TimeSpan LyricPollInterval = TimeSpan.FromMilliseconds(100);

    private string _lyricLine = string.Empty;

    private void OnLyricsChanged(object? sender, EventArgs e)
    {
        // A new document invalidates the line on screen even mid-track, since the
        // lookup may have completed after playback started.
        UpdateLyricLine();
        UpdateLyricPolling();
    }

    /// <summary>
    /// Runs the poll only while it can change something: timed lyrics, playing, and
    /// visible. A paused or lyric-less widget costs nothing.
    /// </summary>
    private LyricsPanel? _panel;

    /// <summary>
    /// Hands the widget the panel to drive. Both need the same poll and the same
    /// position, so one timer serves them rather than each running its own.
    /// </summary>
    public void AttachPanel(LyricsPanel panel) => _panel = panel;

    private void UpdateLyricPolling()
    {
        bool wanted =
            _settings.Current.LyricsEnabled &&
            _lyrics.Current.IsSynced &&
            !_lyrics.Current.IsEmpty &&
            _track?.IsPlaying == true;

        if (wanted == _lyricPoll.IsEnabled) return;

        if (wanted) _lyricPoll.Start();
        else _lyricPoll.Stop();
    }

    private void UpdateLyricLine()
    {
        var document = _lyrics.Current;

        _panel?.Update(_track?.IsPlaying == true);

        var style = _settings.Current.LyricsStyle;
        string line = string.Empty;

        // Only timed lyrics can follow playback, and only the inline mode draws them
        // here — in panel mode the widget keeps showing the title.
        if (document.IsSynced &&
            _media.Clock.IsUsable &&
            _settings.Current.LyricsEnabled &&
            style.Display == LyricsDisplayMode.Inline)
        {
            int index = document.IndexAt(_media.Clock.PositionAt(DateTimeOffset.UtcNow));
            if (index >= 0) line = document.Lines[index].Text;
        }

        line = LyricsTypography.Present(line, style);

        if (line != _lyricLine)
        {
            _lyricLine = line;
            LyricsText.Text = line;
            LyricsHalo.Text = line;

            UpdateTextFades();
        }

        // Outside the guard on purpose. Which layer should be showing does not depend
        // only on the text: moving lyrics to the panel and pausing both leave the line
        // exactly as it was and still mean the title belongs back on screen.
        UpdateTextLayer();
    }

    /// <summary>
    /// Applies the inline lyric appearance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything except the surface: the widget paints no background at all, so the
    /// taskbar's own material shows through — the single decision the whole widget is
    /// built around. A lyric drawn here inherits that rather than overriding it.
    /// </para>
    /// <para>
    /// The effect goes on the halo copy underneath, never on the text. A blur applied
    /// to the live text is not a glow: it is the same glyphs, out of focus. What reads
    /// as a glow is a sharp line sitting on a blurred copy of itself.
    /// </para>
    /// </remarks>
    private void ApplyLyricAppearance()
    {
        var appearance = _settings.Current.LyricsStyle;

        LyricsTypography.ApplyFont(LyricsText, appearance);
        LyricsTypography.ApplyFont(LyricsHalo, appearance);

        LyricsText.Foreground = new SolidColorBrush(LyricsTypography.TextColor(appearance));

        var effect = LyricsTypography.BuildEffect(appearance);

        LyricsHalo.Effect = effect;
        LyricsHalo.Visibility = effect is null ? Visibility.Collapsed : Visibility.Visible;

        if (effect is not null)
            LyricsHalo.Foreground = new SolidColorBrush(LyricsTypography.EffectColor(appearance));
    }

    private void ApplyBarColor() => _barColor.Update(_track?.AlbumArt);

    /// <summary>
    /// Keeps the bar count and the band count in step. The analyser is set first, so
    /// a newly added bar is fed real audio on the frame it appears rather than
    /// climbing up from zero.
    /// </summary>
    private void ApplyBarCount()
    {
        int count = _settings.Current.VisualizerBarCount;

        _analyzer.BandCount = count;
        Bars.BarCount = count;
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
        // The panel goes when the widget goes. A lyric floating over the desktop with
        // nothing on the taskbar under it reads as a stuck window, and the panel's own
        // grace period cannot tell a closed source from the gap between two songs —
        // whereas the debounce that leads here already has.
        _panel?.HideNow();

        if (_hwnd == IntPtr.Zero) return;

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
    }
}
