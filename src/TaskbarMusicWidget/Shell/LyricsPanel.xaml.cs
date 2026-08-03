using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

    /// <summary>
    /// The lime style's panel colour, and the black it is paired with. Flat and
    /// deliberately unsubtle — the whole point of the look.
    /// </summary>
    private static readonly Color LimePanel = Color.FromRgb(0x8A, 0xCE, 0x00);
    private static readonly Color LimeInk = Color.FromRgb(0x10, 0x12, 0x08);

    /// <summary>How dim a word is before it has been sung.</summary>
    private const double UnsungOpacity = 0.38d;

    private IntPtr _hwnd;
    private IntPtr _ownerHandle;
    private bool _acrylic;
    private int _lineIndex = -2;
    private bool _wantVisible;

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
    /// colour as it is sung, so the others are shared and never touched again.
    /// </summary>
    private readonly SolidColorBrush _activeBrush = new();

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

        _accent.Update(_media.Current?.AlbumArt);

        ApplyStyle();

        // Force the line to be rebuilt on the next poll. A style change alters the
        // text itself, not just its colour, so repainting the existing runs would
        // leave the old casing on screen until the line happened to change.
        _lineIndex = -2;
        _activeWord = -1;
        PaintWords(0d);
    }

    /// <summary>
    /// Applies the chosen style: the panel surface, the typeface, and whether the
    /// halo layer is used.
    /// </summary>
    private void ApplyStyle()
    {
        var style = _settings.Current.LyricsStyle;

        bool lime = style == LyricsStyle.Lime;

        // Lime replaces the system surface outright — a flat colour is the look, and
        // it also means the black text has a known background rather than acrylic.
        Root.Background = lime
            ? new SolidColorBrush(LimePanel)
            : _acrylic
                ? Brushes.Transparent
                : new SolidColorBrush(SystemBackdrop.Fallback(_theme.BackdropEstimate));

        var context = lime
            ? new SolidColorBrush(Color.FromArgb(0x99, LimeInk.R, LimeInk.G, LimeInk.B))
            : _theme.TextSecondary;

        PreviousLine.Foreground = context;
        NextLine.Foreground = context;

        var typeface = lime
            ? new FontFamily("Arial Narrow, Segoe UI Variable Display, Segoe UI")
            : new FontFamily("Segoe UI Variable Display, Segoe UI");

        CurrentLine.FontFamily = typeface;
        HaloLine.FontFamily = typeface;
        PreviousLine.FontFamily = typeface;
        NextLine.FontFamily = typeface;

        // The halo carries the effect so the live text never does. An effect on the
        // text itself would be re-rendered every frame as the word highlight moves,
        // where this rasterises once per line and then sits there.
        if (style == LyricsStyle.Glow)
        {
            HaloLine.Visibility = Visibility.Visible;
            HaloLine.Foreground = new SolidColorBrush(WordBrush(sung: true).Color);
            HaloLine.Effect = new BlurEffect { Radius = 16d, KernelType = KernelType.Gaussian };
        }
        else if (lime)
        {
            // Softens the edges into the flat colour, which is most of what makes the
            // look read as printed rather than rendered.
            HaloLine.Visibility = Visibility.Visible;
            HaloLine.Foreground = new SolidColorBrush(LimeInk);
            HaloLine.Effect = new BlurEffect { Radius = 4d, KernelType = KernelType.Gaussian };
        }
        else
        {
            HaloLine.Visibility = Visibility.Collapsed;
            HaloLine.Effect = null;
        }
    }

    /// <summary>
    /// The colour a word takes before and after it is sung.
    /// </summary>
    /// <remarks>
    /// Outside the lime style this is the same colour the bars use, corrected against
    /// the same backdrop estimate the acrylic approximates — which ties the two halves
    /// of the widget together and is already guaranteed to clear 3:1, the right
    /// threshold for text this large.
    /// </remarks>
    private SolidColorBrush WordBrush(bool sung)
    {
        var color = _settings.Current.LyricsStyle == LyricsStyle.Lime
            ? LimeInk
            : ((SolidColorBrush)_accent.Brush).Color;

        if (sung) return new SolidColorBrush(color);

        return new SolidColorBrush(
            Color.FromArgb((byte)(0xFF * UnsungOpacity), color.R, color.G, color.B));
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

            if (wanted) StartSweep();
            else StopSweep();
        }

        if (!wanted) return;

        int index = document.IndexAt(_media.Clock.PositionAt(DateTimeOffset.UtcNow));
        if (index == _lineIndex) return;

        _lineIndex = index;

        PreviousLine.Text = TextAt(document, index - 1);
        NextLine.Text = TextAt(document, index + 1);

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

    // ---- Word sweep --------------------------------------------------------

    /// <summary>
    /// Lays the current line out as one <see cref="Run"/> per word, so each can be
    /// lit independently.
    /// </summary>
    /// <remarks>
    /// Runs rather than a gradient mask over the whole line. A horizontal gradient
    /// assumes the line is one row, and this one wraps to two — the sweep would run
    /// across both at once. Runs also cost nothing to re-colour, where a mask would
    /// need the pixel position of every word measured.
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

        // A file that timed its own words is always right; ours is an estimate.
        _words = line.Words ?? WordTiming.Estimate(text, line.Start, document.EndOf(index));

        _runs = new Run[_words.Count];

        for (int i = 0; i < _words.Count; i++)
        {
            // The trailing space belongs to the run, so word spacing survives being
            // split up and the line measures exactly as the unsplit text would.
            string word = Present(_words[i].Text);
            _runs[i] = new Run(i + 1 < _words.Count ? word + " " : word);
            CurrentLine.Inlines.Add(_runs[i]);
        }

        _activeWord = -1;
        PaintWords(0d);
    }

    /// <summary>
    /// Colours the line for a given point in it. Cheap enough to run every frame:
    /// brushes are only reassigned when the word changes, and between words only one
    /// brush's colour moves.
    /// </summary>
    private void PaintWords(double progressIntoWord)
    {
        if (_runs.Length == 0) return;

        var sung = WordBrush(sung: true);
        var unsung = WordBrush(sung: false);

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
        if (!_wantVisible)
        {
            StopSweep();
            return;
        }

        PaintWords(ProgressIntoWord(_media.Clock.PositionAt(DateTimeOffset.UtcNow)));
    }

    private void StartSweep()
    {
        if (_sweeping) return;
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
        Present(index >= 0 && index < document.Lines.Count
            ? document.Lines[index].Text
            : string.Empty);

    /// <summary>
    /// Applies any casing the style calls for. The lime look is lowercase throughout —
    /// it is as much a part of it as the colour is.
    /// </summary>
    private string Present(string text) =>
        _settings.Current.LyricsStyle == LyricsStyle.Lime ? text.ToLowerInvariant() : text;

    /// <summary>Re-resolves the accent when the track, and so the artwork, changes.</summary>
    public void OnTrackChanged()
    {
        _accent.Update(_media.Current?.AlbumArt);
        DebugLog.Write($"lyrics panel: acrylic={_acrylic}");
    }
}
