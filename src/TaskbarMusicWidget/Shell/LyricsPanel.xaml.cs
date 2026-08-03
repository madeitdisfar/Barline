using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarMusicWidget.Diagnostics;
using TaskbarMusicWidget.Lyrics;
using TaskbarMusicWidget.Media;
using TaskbarMusicWidget.Settings;
using TaskbarMusicWidget.Ui;
using static TaskbarMusicWidget.Shell.NativeMethods;

namespace TaskbarMusicWidget.Shell;

/// <summary>
/// A floating panel that shows the current lyric line just above the taskbar, with
/// the lines either side of it for context.
/// </summary>
/// <remarks>
/// <para>
/// The inline display has about 150 logical pixels to work with — twenty-five
/// characters — which is not enough for a lyric. This exists because the aesthetic
/// version of the feature needs room, and it is a separate window because the taskbar
/// has none to give.
/// </para>
/// <para>
/// It never takes input. <c>WS_EX_TRANSPARENT</c> passes clicks straight through, so
/// a panel sitting over the desktop cannot swallow a click meant for whatever is
/// underneath — the failure that would make a floating overlay intolerable.
/// </para>
/// <para>
/// Like the widget it is owned by the taskbar, which is what makes it disappear for
/// fullscreen apps and slide away with auto-hide without any handling of its own.
/// </para>
/// </remarks>
internal partial class LyricsPanel : Window
{
    /// <summary>
    /// Sized against real lyrics rather than by eye. At 380px a line as ordinary as
    /// "So tell me that you love me again" was already being cut, and an ellipsis in
    /// the middle of a sung line is worse than no lyrics at all.
    /// </summary>
    private const double PanelLogicalWidth = 520d;

    /// <summary>
    /// Fixed, and tall enough for the current line to take two rows. Sizing to the
    /// content would make the panel jump every few seconds as lines changed length,
    /// which is exactly the kind of movement the widget avoids elsewhere.
    /// </summary>
    private const double PanelLogicalHeight = 136d;

    /// <summary>Clearance between the panel and the top of the taskbar.</summary>
    private const double GapLogical = 10d;

    private readonly TaskbarTracker _tracker;
    private readonly MediaSessionService _media;
    private readonly Theme _theme;
    private readonly SettingsStore _settings;
    private readonly LyricsService _lyrics;

    /// <summary>
    /// The panel's own resolver, as the settings window has. Sharing one would mean
    /// two windows animating the same brush and fighting over it.
    /// </summary>
    private readonly BarColorResolver _accent;

    private IntPtr _hwnd;
    private IntPtr _ownerHandle;
    private bool _acrylic;
    private int _lineIndex = -2;
    private bool _wantVisible;

    public LyricsPanel(
        TaskbarTracker tracker,
        MediaSessionService media,
        Theme theme,
        SettingsStore settings,
        LyricsService lyrics)
    {
        _tracker = tracker;
        _media = media;
        _theme = theme;
        _settings = settings;
        _lyrics = lyrics;
        _accent = new BarColorResolver(theme, settings);

        InitializeComponent();

        Width = PanelLogicalWidth;
        Height = PanelLogicalHeight;

        _tracker.Changed += (_, state) => Place(state);
        _theme.Changed += (_, _) => ApplyTheme();
        _settings.Changed += (_, _) => ApplyTheme();

        ApplyTheme();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TRANSPARENT is the important one here: the panel is a display, not a
        // control, and must never intercept a click aimed at what is behind it.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));

        var source = HwndSource.FromHwnd(_hwnd);
        if (source?.CompositionTarget is not null)
        {
            // Without this WPF clears the client area to opaque black and the
            // extended frame never shows through.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        ApplyBackdrop();
        Place(_tracker.Current);
    }

    // ---- Appearance --------------------------------------------------------

    private void ApplyBackdrop()
    {
        _acrylic = SystemBackdrop.TryApply(_hwnd, _theme.IsLight);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (_hwnd != IntPtr.Zero)
            _acrylic = SystemBackdrop.TryApply(_hwnd, _theme.IsLight);

        // On acrylic the compositor supplies the surface; without it the panel has to
        // paint one, or the text would sit on whatever is behind with no guarantee at
        // all about contrast.
        Root.Background = _acrylic
            ? Brushes.Transparent
            : new SolidColorBrush(SystemBackdrop.Fallback(_theme.BackdropEstimate));

        PreviousLine.Foreground = _theme.TextSecondary;
        NextLine.Foreground = _theme.TextSecondary;

        // The current line carries the same colour as the bars, corrected against the
        // same backdrop estimate the acrylic approximates. It ties the two halves of
        // the widget together, and it is already guaranteed to clear 3:1 — which is
        // the right threshold because the line is rendered as large text.
        _accent.Update(_media.Current?.AlbumArt);
        CurrentLine.Foreground = _accent.Brush;
    }

    // ---- Placement ---------------------------------------------------------

    /// <summary>
    /// Sits the panel directly above the widget, tracking the taskbar exactly as the
    /// widget does so the two move as one.
    /// </summary>
    private void Place(TaskbarState state)
    {
        if (_hwnd == IntPtr.Zero) return;

        if (!_wantVisible || !state.IsAvailable || !state.ShouldShow)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            return;
        }

        EnsureTaskbarOwnership();

        double scale = state.Dpi / 96d;

        int width = (int)Math.Round(PanelLogicalWidth * scale);
        int height = (int)Math.Round(PanelLogicalHeight * scale);
        int gap = (int)Math.Round(GapLogical * scale);

        int x = state.Rect.Left;
        int y = state.Rect.Top - gap - height;

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Owning the panel to the taskbar is what makes it ride the taskbar's z-order,
    /// hide behind fullscreen apps, and slide away with auto-hide — the same
    /// mechanism the widget uses, for the same reasons.
    /// </summary>
    private void EnsureTaskbarOwnership()
    {
        IntPtr taskbar = _tracker.TaskbarHandle;
        if (taskbar == IntPtr.Zero || taskbar == _ownerHandle) return;

        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, taskbar);
        _ownerHandle = taskbar;
    }

    // ---- Content -----------------------------------------------------------

    /// <summary>
    /// Called by the widget on every lyric poll. Cheap when nothing has changed,
    /// which is almost always.
    /// </summary>
    public void Update(bool playing)
    {
        var document = _lyrics.Current;

        bool wanted =
            _settings.Current.LyricsEnabled &&
            _settings.Current.LyricsDisplay == LyricsDisplayMode.Panel &&
            document.IsSynced &&
            !document.IsEmpty &&
            playing &&
            _media.Clock.IsUsable;

        if (wanted != _wantVisible)
        {
            _wantVisible = wanted;
            _lineIndex = -2;
            Place(_tracker.Current);
        }

        if (!wanted) return;

        int index = document.IndexAt(_media.Clock.PositionAt(DateTimeOffset.UtcNow));
        if (index == _lineIndex) return;

        _lineIndex = index;

        PreviousLine.Text = TextAt(document, index - 1);
        CurrentLine.Text = TextAt(document, index);
        NextLine.Text = TextAt(document, index + 1);

        // A short fade rather than a hard swap. At line-level timing the change lands
        // on a beat, and cutting looks like a glitch where a fade looks intended.
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(
            0.35d, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(
            1d,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.NormalMs)),
            Motion.Standard));

        CurrentLine.BeginAnimation(OpacityProperty, fade);
    }

    private static string TextAt(LyricsDocument document, int index) =>
        index >= 0 && index < document.Lines.Count ? document.Lines[index].Text : string.Empty;

    /// <summary>Re-resolves the accent when the track, and so the artwork, changes.</summary>
    public void OnTrackChanged()
    {
        _accent.Update(_media.Current?.AlbumArt);
        DebugLog.Write($"lyrics panel: acrylic={_acrylic}");
    }
}
