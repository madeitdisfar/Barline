using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Barline.Lyrics;
using Barline.Media;
using Barline.Settings;
using Barline.Ui;
using static Barline.Shell.NativeMethods;

namespace Barline.Shell;

/// <summary>
/// A floating panel showing the line of the song being sung right now.
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
/// </remarks>
internal partial class LyricsPanel : Window
{
    /// <summary>Clearance from whichever screen edge the panel is anchored to.</summary>
    private const double MarginLogical = 10d;

    /// <summary>
    /// How long the panel waits before disappearing once there is nothing to show.
    /// </summary>
    /// <remarks>
    /// Between tracks there is a moment with no lyrics: the old ones are dropped and
    /// the new ones have not arrived. Hiding immediately made the panel blink out and
    /// back on every song change. Showing is never delayed — only hiding.
    /// </remarks>
    private static readonly TimeSpan HideGrace = TimeSpan.FromSeconds(4);

    /// <summary>How faint the panel goes while the pointer is over it.</summary>
    private const double HoverFadeOpacity = 0.15d;

    private readonly TaskbarTracker _tracker;
    private readonly MediaSessionService _media;
    private readonly SettingsStore _settings;
    private readonly LyricsService _lyrics;

    /// <summary>Delays hiding so a song change does not blink the panel out.</summary>
    private readonly DispatcherTimer _hideDebounce;

    private IntPtr _hwnd;
    private IntPtr _ownerHandle;
    private int _lineIndex = -2;
    private bool _wantVisible;
    private bool _shown;
    private bool _hovered;
    private int _placedX;
    private int _placedY;
    private int _placedWidth;
    private int _placedHeight;

    /// <summary>Carries the panel across with the widget. See <see cref="Slide"/>.</summary>
    private readonly Slide _slide = new();

    private RECT _placedAgainst;
    private bool _onScreen;

    /// <summary>
    /// The current line split into words, with a start for each — taken from the file
    /// when it carries word timing, and estimated from the line when it does not.
    /// </summary>
    private IReadOnlyList<LyricWord> _words = [];
    private Run[] _runs = [];
    private int _activeWord = -1;
    private bool _sweeping;

    /// <summary>
    /// Rebuilt per line rather than per frame. Only the active word's brush changes
    /// color as it is sung, so the others are shared and never touched again.
    /// </summary>
    private readonly SolidColorBrush _activeBrush = new();

    private LyricsAppearance Appearance => _settings.Current.LyricsStyle;

    public LyricsPanel(
        TaskbarTracker tracker,
        MediaSessionService media,
        SettingsStore settings,
        LyricsService lyrics)
    {
        _tracker = tracker;
        _media = media;
        _settings = settings;
        _lyrics = lyrics;

        InitializeComponent();

        _hideDebounce = new DispatcherTimer(DispatcherPriority.Normal) { Interval = HideGrace };
        _hideDebounce.Tick += (_, _) =>
        {
            _hideDebounce.Stop();
            _shown = false;
            Place(_tracker.Current);
            StopSweep();
        };

        _tracker.Changed += (_, state) => Place(state);
        _settings.Changed += (_, _) =>
        {
            ApplyAppearance();
            Place(_tracker.Current);
        };

        ApplyAppearance();
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

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        ApplyAppearance();
        Place(_tracker.Current);
    }

    /// <summary>
    /// Re-places the panel after a change of display scale.
    /// </summary>
    /// <remarks>
    /// The panel is sized in physical pixels by <see cref="Place"/>, and WPF answers
    /// the DPI change that a move to another display raises by rescaling the window it
    /// was just given. Measured on a 150% second display: a panel placed at 390x150 was
    /// resized to 293x113, which is 390x150 times the ratio between the two displays'
    /// scales, and stayed that way because nothing placed it again. Placing it once
    /// more after WPF has finished its own bookkeeping is what the widget does, for the
    /// same reason.
    /// </remarks>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED)
            Dispatcher.BeginInvoke(new Action(() => Place(_tracker.Current)));

        return IntPtr.Zero;
    }

    // ---- Appearance --------------------------------------------------------

    /// <summary>
    /// Realizes the current appearance: surface, type, color and effect.
    /// </summary>
    private void ApplyAppearance()
    {
        var appearance = Appearance;

        ApplySurface(appearance);

        LyricsTypography.ApplyFont(CurrentLine, appearance);
        LyricsTypography.ApplyFont(HaloLine, appearance);

        // The ellipsis a trimmed line ends with is drawn in the TextBlock's own
        // Foreground, not in the runs' — leaving it unset drew it in the inherited
        // default, which is black and effectively invisible on a dark panel.
        CurrentLine.Foreground = new SolidColorBrush(LyricsTypography.TextColor(appearance));

        var effect = LyricsTypography.BuildEffect(appearance);

        if (effect is null)
        {
            HaloLine.Visibility = Visibility.Collapsed;
            HaloLine.Effect = null;
        }
        else
        {
            HaloLine.Visibility = Visibility.Visible;
            HaloLine.Effect = effect;
            HaloLine.Foreground = new SolidColorBrush(LyricsTypography.EffectColor(appearance));
        }

        // The text itself changes with the appearance (casing), so the line has to be
        // rebuilt rather than merely recolored.
        _lineIndex = -2;
        _activeWord = -1;
        PaintWords(0d);
    }

    private void ApplySurface(LyricsAppearance appearance)
    {
        Root.CornerRadius = new CornerRadius(appearance.CornerRadius);

        var color = LyricsTypography.Parse(appearance.BackgroundColor, Color.FromRgb(0x2C, 0x2C, 0x2C));
        byte alpha = (byte)Math.Round(255d * Math.Clamp(appearance.BackgroundOpacity, 0d, 1d));

        // Every background is painted by WPF, into a window that is transparent by
        // per-pixel alpha. That is what lets the corner radius apply to all of them
        // alike — the compositor-blurred option that could not be rounded is gone.
        Root.Background = appearance.Background switch
        {
            LyricsBackground.None => Brushes.Transparent,
            LyricsBackground.Solid => new SolidColorBrush(color),
            _ => new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
        };
    }

    // ---- Placement ---------------------------------------------------------

    /// <summary>
    /// Positions the panel according to the chosen anchor, at the chosen size.
    /// </summary>
    /// <remarks>
    /// Every anchor is measured from the taskbar's own monitor, so the panel follows
    /// the taskbar rather than assuming the primary screen — and it moves with the
    /// taskbar when it is auto-hidden or the resolution changes.
    /// </remarks>
    private void Place(TaskbarState state)
    {
        if (_hwnd == IntPtr.Zero) return;

        bool hidden =
            !_shown ||
            !state.IsAvailable ||
            !state.ShouldShow ||
            (_hovered && _settings.Current.LyricsHover == LyricsHoverBehavior.Hide);

        if (hidden)
        {
            _slide.Stop();
            _onScreen = false;

            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
            return;
        }

        EnsureTaskbarOwnership();

        double scale = state.Dpi / 96d;
        var style = Appearance;

        int width = (int)Math.Round(style.PanelWidth * scale);
        int height = (int)Math.Round(style.PanelHeight * scale);
        int margin = (int)Math.Round(MarginLogical * scale);

        var screen = ScreenOf(state);

        int x;
        int y;

        switch (style.Position)
        {
            case LyricsPanelPosition.BottomCenter:
                x = screen.Left + ((screen.Width - width) / 2);
                y = state.Rect.Top - margin - height;
                break;

            case LyricsPanelPosition.TopCenter:
                x = screen.Left + ((screen.Width - width) / 2);
                y = screen.Top + margin;
                break;

            case LyricsPanelPosition.Custom:
                // The panel's own size comes off the travel, so 100% is flush with the
                // far edge rather than one panel-width past it.
                x = screen.Left + (int)Math.Round((screen.Width - width) * style.CustomX / 100d);
                y = screen.Top + (int)Math.Round((screen.Height - height) * style.CustomY / 100d);
                break;

            default:
                // Above the widget, at the same end of the screen the widget is at, and
                // clear of that edge by the same margin it clears the taskbar by. Flush
                // against the side put a rounded corner into the corner of the screen,
                // where it read as a panel that had been cut off rather than placed.
                // The panel is narrower than the widget, so it hangs from the screen
                // edge rather than lining up with the widget's other side: the widget
                // is what it belongs to, but the screen is what it is measured from.
                x = state.WidgetAtFarEnd(
                        (int)Math.Round(OverlayWindow.WidgetLogicalWidth * scale))
                    ? screen.Right - width - margin
                    : screen.Left + margin;
                y = state.Rect.Top - margin - height;
                break;
        }

        // Alongside the widget, and only when following it is what moved the panel.
        // The anchors that do not track the taskbar are set from the settings window,
        // where a slider is being dragged and a quarter second of easing behind every
        // step would feel like lag rather than polish.
        bool follows =
            _onScreen &&
            style.Position == LyricsPanelPosition.AboveWidget &&
            x != _placedX &&
            y == _placedY &&
            width == _placedWidth &&
            height == _placedHeight &&
            state.Rect.Equals(_placedAgainst);

        if (follows)
        {
            _slide.Run(_hwnd, x, y);
        }
        else
        {
            _slide.Stop();

            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        _placedAgainst = state.Rect;
        _onScreen = true;

        _placedX = x;
        _placedY = y;
        _placedWidth = width;
        _placedHeight = height;

        // Bound the text to what the panel can actually show, so a line that wraps
        // further than the panel is trimmed rather than spilling past its edge.
        CurrentLine.MaxHeight = Math.Max(
            style.FontSize,
            style.PanelHeight - (Root.Padding.Top + Root.Padding.Bottom));

        HaloLine.MaxHeight = CurrentLine.MaxHeight;
    }

    /// <summary>Bounds of the monitor the taskbar is on.</summary>
    /// <remarks>
    /// Resolved from the taskbar's own window rather than the primary monitor, so the
    /// panel lands on the same screen as the widget wherever that turns out to be.
    /// </remarks>
    private RECT ScreenOf(TaskbarState state)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        IntPtr monitor = MonitorFromWindow(_tracker.TaskbarHandle, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            return info.rcMonitor;

        // The taskbar spans its monitor horizontally, so its own rect is a usable
        // approximation if the monitor cannot be queried.
        return state.Rect;
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

    // ---- Hover -------------------------------------------------------------

    /// <summary>
    /// Notices the pointer entering and leaving the panel's rectangle.
    /// </summary>
    /// <remarks>
    /// Polled from the lyric tick rather than handled as mouse events, because
    /// <c>WS_EX_TRANSPARENT</c> means this window never receives any. That is also
    /// what makes the behavior worth having: the panel cannot be clicked away, so it
    /// needs some way to stop covering what is under it.
    /// </remarks>
    private void UpdateHover()
    {
        var behavior = _settings.Current.LyricsHover;

        // Against the rect actually passed to SetWindowPos, in physical pixels. The
        // window's own Left/Top are WPF's idea of where it is and do not follow a
        // position set through the Win32 call.
        bool over = behavior != LyricsHoverBehavior.None &&
            _shown &&
            _placedWidth > 0 &&
            GetCursorPos(out var cursor) &&
            cursor.X >= _placedX && cursor.X < _placedX + _placedWidth &&
            cursor.Y >= _placedY && cursor.Y < _placedY + _placedHeight;

        if (over == _hovered) return;
        _hovered = over;

        switch (behavior)
        {
            case LyricsHoverBehavior.Hide:
                Place(_tracker.Current);
                break;

            case LyricsHoverBehavior.Fade:
                Fade(over ? HoverFadeOpacity : 1d);
                break;
        }
    }

    /// <summary>Physical pixels per logical pixel, from the taskbar's DPI.</summary>
    private double DpiScale => _tracker.Current.Dpi / 96d;

    private void Fade(double to)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.FastMs)),
            Motion.Standard));

        Root.BeginAnimation(OpacityProperty, animation);
    }

    // ---- Content -----------------------------------------------------------

    /// <summary>
    /// Called by the widget on every lyric poll. Cheap when nothing has changed,
    /// which is almost always.
    /// </summary>
    public void Update(bool playing)
    {
        var document = _lyrics.Current;

        // Whether the panel is wanted at all, as against merely having nothing to show
        // this instant. Turning lyrics off, or moving them into the widget, is a
        // decision — it should take effect at once rather than linger for the grace
        // period that exists to cover the gap between songs.
        bool enabled =
            _settings.Current.LyricsEnabled &&
            Appearance.Display == LyricsDisplayMode.Panel;

        bool wanted =
            enabled &&
            document.IsSynced &&
            !document.IsEmpty &&
            playing &&
            _media.Clock.IsUsable;

        if (!enabled && _shown)
        {
            HideNow();
            return;
        }

        if (wanted != _wantVisible)
        {
            _wantVisible = wanted;

            if (wanted)
            {
                // Showing is immediate, and cancels any pending hide — which is what
                // carries the panel across a song change without a blink.
                _hideDebounce.Stop();
                _lineIndex = -2;
                _shown = true;
                Place(_tracker.Current);
                StartSweep();
            }
            else if (!_hideDebounce.IsEnabled)
            {
                _hideDebounce.Start();
            }
        }

        UpdateHover();

        if (!wanted) return;

        int index = document.IndexAt(_media.Clock.PositionAt(DateTimeOffset.UtcNow));
        if (index == _lineIndex) return;

        _lineIndex = index;

        BuildLine(document, index);

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

    /// <summary>
    /// Takes the panel off screen at once, skipping the grace period.
    /// </summary>
    /// <remarks>
    /// For the two cases where waiting is wrong. Turning lyrics off is a decision, and
    /// a decision should land immediately. A source app closing is not a gap between
    /// songs — there is no next song coming, so the grace period would only leave a
    /// lyric floating over the desktop after the widget it belongs to has gone. The
    /// widget calls this from its own hide, which has already absorbed the momentary
    /// nulls a track change produces, so by the time it runs the source is really gone.
    /// </remarks>
    public void HideNow()
    {
        if (!_shown && !_hideDebounce.IsEnabled) return;

        _hideDebounce.Stop();
        _wantVisible = false;
        _shown = false;
        Place(_tracker.Current);
        StopSweep();
    }

    // ---- Word sweep --------------------------------------------------------

    /// <summary>
    /// Lays the current line out as one <see cref="Run"/> per word, so each can be
    /// lit independently.
    /// </summary>
    /// <remarks>
    /// Runs rather than a gradient mask over the whole line. A horizontal gradient
    /// assumes the line is one row, and this one can wrap — the sweep would run across
    /// both at once. Runs also cost nothing to re-color, where a mask would need the
    /// pixel position of every word measured.
    /// </remarks>
    private void BuildLine(LyricsDocument document, int index)
    {
        string text = TextAt(document, index);

        CurrentLine.Inlines.Clear();
        HaloLine.Text = text;

        if (text.Length == 0)
        {
            _words = [];
            _runs = [];
            _activeWord = -1;
            return;
        }

        var line = document.Lines[index];

        if (!_settings.Current.LyricsWordByWord)
        {
            // Line at a time: one run for the whole line, lit from the moment it
            // starts. The sweep machinery below then has nothing to do.
            _words = [new LyricWord(line.Start, text)];
            _runs = [new Run(text)];
            CurrentLine.Inlines.Add(_runs[0]);
            _activeWord = -1;
            PaintWords(1d);
            return;
        }

        // A file that timed its own words is always right; ours is an estimate.
        _words = line.Words ?? WordTiming.Estimate(text, line.Start, document.EndOf(index));

        _runs = new Run[_words.Count];

        for (int i = 0; i < _words.Count; i++)
        {
            // The trailing space belongs to the run, so word spacing survives being
            // split up and the line measures exactly as the unsplit text would.
            string word = LyricsTypography.Present(_words[i].Text, Appearance);
            _runs[i] = new Run(i + 1 < _words.Count ? word + " " : word);
            CurrentLine.Inlines.Add(_runs[i]);
        }

        _activeWord = -1;
        PaintWords(0d);
    }

    /// <summary>
    /// Colors the line for a given point in it. Cheap enough to run every frame:
    /// brushes are only reassigned when the word changes, and between words only one
    /// brush's color moves.
    /// </summary>
    private void PaintWords(double progressIntoWord)
    {
        if (_runs.Length == 0) return;

        var appearance = Appearance;
        var sung = new SolidColorBrush(LyricsTypography.TextColor(appearance));
        var unsung = new SolidColorBrush(LyricsTypography.UnsungColor(appearance));

        int active = ActiveWordAt(_media.Clock.PositionAt(DateTimeOffset.UtcNow));

        if (active != _activeWord)
        {
            _activeWord = active;

            for (int i = 0; i < _runs.Length; i++)
            {
                _runs[i].Foreground = i < active ? sung
                    : i == active ? _activeBrush
                    : unsung;
            }
        }

        if (active < 0) return;

        // Within a word, ease from unsung to sung rather than switching outright. At
        // line-level source timing the word boundaries are estimated anyway, and a
        // hard step advertises exactly where the estimate is wrong.
        _activeBrush.Color = Blend(unsung.Color, sung.Color, Math.Clamp(progressIntoWord, 0d, 1d));
    }

    private int ActiveWordAt(TimeSpan position)
    {
        int active = -1;

        for (int i = 0; i < _words.Count; i++)
        {
            if (_words[i].Start <= position) active = i;
            else break;
        }

        return active;
    }

    /// <summary>How far through the active word playback is, as 0..1.</summary>
    private double ProgressIntoWord(TimeSpan position)
    {
        if (_activeWord < 0 || _activeWord >= _words.Count) return 0d;

        var from = _words[_activeWord].Start;
        var to = _activeWord + 1 < _words.Count
            ? _words[_activeWord + 1].Start
            : from + TimeSpan.FromMilliseconds(400);

        double span = (to - from).TotalSeconds;

        return span <= 0d ? 1d : (position - from).TotalSeconds / span;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_shown)
        {
            StopSweep();
            return;
        }

        PaintWords(ProgressIntoWord(_media.Clock.PositionAt(DateTimeOffset.UtcNow)));
    }

    private void StartSweep()
    {
        // Line at a time needs no per-frame work at all.
        if (_sweeping || !_settings.Current.LyricsWordByWord) return;

        _sweeping = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopSweep()
    {
        if (!_sweeping) return;
        _sweeping = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (byte)(from.A + ((to.A - from.A) * amount)),
        (byte)(from.R + ((to.R - from.R) * amount)),
        (byte)(from.G + ((to.G - from.G) * amount)),
        (byte)(from.B + ((to.B - from.B) * amount)));

    private string TextAt(LyricsDocument document, int index) =>
        LyricsTypography.Present(
            index >= 0 && index < document.Lines.Count ? document.Lines[index].Text : string.Empty,
            Appearance);
}
