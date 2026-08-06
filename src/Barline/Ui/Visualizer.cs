using System.Windows;
using System.Windows.Media;

namespace Barline.Ui;

/// <summary>
/// The Apple-Music-style bar visualiser: a small row of rounded bars that respond
/// to what is playing.
/// </summary>
/// <remarks>
/// <para>
/// The element owns only presentation — smoothing, resting behaviour and drawing.
/// Its input is either an external spectrum (see <see cref="SetLevels"/>, driven by
/// the FFT) or a built-in decorative motion used when no audio source is available.
/// Keeping the two interchangeable means the render path is identical either way.
/// </para>
/// <para>
/// Bars never flatline and never thrash: levels are clamped to
/// <see cref="MinFraction"/>..1 and smoothed with a fast attack and slow decay, so
/// quiet passages still read as alive and loud ones stay legible rather than
/// strobing.
/// </para>
/// </remarks>
internal sealed class Visualizer : FrameworkElement
{
    /// <summary>Bar count when nothing says otherwise — the shipped design.</summary>
    public const int DefaultBarCount = 4;

    /// <summary>
    /// Total bar area, in logical pixels, split between ink and gaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both figures are held constant as the bar count changes, which is what keeps
    /// every count looking like the same widget. The original four 3px bars with 3px
    /// gaps give 12px of ink and 9px of gap; a wider count divides those same two
    /// budgets more finely rather than growing.
    /// </para>
    /// <para>
    /// Holding the width alone would be enough to stop the widget stretching, but it
    /// would let more bars mean more ink, so a "detailed" visualiser would also read
    /// as a heavier one. Fixing the ink too means changing the count changes the
    /// detail and nothing else — the row keeps its weight against the taskbar, and
    /// the bar colour keeps the contrast it was corrected for.
    /// </para>
    /// </remarks>
    private const double TotalBarInk = 12d;
    private const double TotalBarGap = 9d;

    /// <summary>
    /// Resting height. Must stay comfortably above the bar width: with fully rounded
    /// caps, a bar shorter than it is wide stops reading as a bar and turns into a
    /// dot. Held constant across counts, so the resting row is always the same line.
    /// </summary>
    private const double MinBarHeight = 6d;
    private const double MaxBarHeight = 18d;

    // Time constants, in seconds. Attack is quick enough to catch transients;
    // decay is slow enough that the motion reads as fluid rather than twitchy.
    private const double AttackTau = 0.035d;
    private const double DecayTau = 0.220d;

    private int _barCount = DefaultBarCount;
    private double[] _current = new double[DefaultBarCount];
    private double[] _target = new double[DefaultBarCount];

    // Deliberately irrational-ish ratios so the decorative loop never lines up
    // into an obvious repeating pattern. The first four are the shipped values, so
    // the default count animates exactly as it always has.
    private static readonly double[] DecorativeRates =
        [2.9d, 3.7d, 2.3d, 3.1d, 3.5d, 2.6d, 3.3d, 2.8d];
    private static readonly double[] DecorativePhases =
        [0.0d, 1.7d, 3.4d, 5.1d, 0.9d, 2.6d, 4.3d, 6.0d];

    private TimeSpan _lastFrame;
    private double _elapsed;
    private bool _subscribed;

    public Visualizer()
    {
        // Constant regardless of count: the bars divide a fixed budget rather than
        // extending past it, so the reserved zone never has to reflow.
        Width = TotalWidth;   // 21px
        Height = MaxBarHeight;
        IsHitTestVisible = false;

        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();

        // The widget is hidden whenever a fullscreen app is foreground or nothing
        // is playing. Without this the frame loop would keep animating bars that
        // nobody can see.
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    // ---- Public surface ---------------------------------------------------

    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(
            nameof(BarBrush), typeof(Brush), typeof(Visualizer),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>
    /// How many bars are drawn. Bars share a fixed width and ink budget, so a higher
    /// count means thinner bars in the same footprint rather than a wider widget.
    /// </summary>
    /// <remarks>
    /// Levels already in flight are kept where they overlap, so changing the count
    /// while playing grows or trims the row rather than dropping every bar to rest
    /// and climbing back up.
    /// </remarks>
    public int BarCount
    {
        get => _barCount;
        set
        {
            if (_barCount == value) return;

            _barCount = value;
            _current = Resize(_current, value);
            _target = Resize(_target, value);

            InvalidateVisual();
        }
    }

    /// <summary>
    /// Bar width and gap for a given count, in logical pixels.
    /// </summary>
    /// <remarks>
    /// A pure function of the count, and the whole of the sizing rule: both budgets
    /// are fixed, so the count only decides how finely they are cut. Separated from
    /// <see cref="OnRender"/> so the invariants it exists to hold — constant total
    /// width, constant total ink — can be asserted directly.
    /// </remarks>
    public static (double Width, double Gap) BarGeometry(int barCount) =>
        (TotalBarInk / barCount, barCount > 1 ? TotalBarGap / (barCount - 1) : 0d);

    /// <summary>Total width the bars occupy, whatever the count.</summary>
    public static double TotalWidth => TotalBarInk + TotalBarGap;

    private static double[] Resize(double[] source, int length)
    {
        var resized = new double[length];
        source.AsSpan(0, Math.Min(source.Length, length)).CopyTo(resized);
        return resized;
    }

    private bool _isActive;

    /// <summary>
    /// True while media is playing. When false the bars ease down to rest and the
    /// per-frame callback detaches, so a paused widget costs nothing.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            UpdateSubscription();
        }
    }

    /// <summary>
    /// Optional source of real spectrum data. It fills the supplied array with
    /// levels in 0..1 and returns true when it has data.
    /// </summary>
    /// <remarks>
    /// When this is absent or returns false the decorative motion takes over, so
    /// the fallback is automatic if audio capture is unavailable or drops out
    /// mid-track. Nothing else needs to know which source is driving the bars.
    /// </remarks>
    public Func<double[], bool>? LevelSource { get; set; }

    // ---- Frame loop -------------------------------------------------------

    /// <summary>
    /// Starts the frame loop. It runs while active to animate, and while inactive
    /// to ease the bars down — <see cref="OnRendering"/> detaches itself once an
    /// inactive visualiser has settled, or as soon as it stops being visible.
    /// </summary>
    private void UpdateSubscription()
    {
        if (IsVisible) Subscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args) return;

        // Nothing to animate while off-screen; re-armed by IsVisibleChanged.
        if (!IsVisible)
        {
            Unsubscribe();
            return;
        }

        // WPF can raise Rendering more than once for the same frame.
        if (args.RenderingTime == _lastFrame) return;

        double dt = _lastFrame == TimeSpan.Zero
            ? 1d / 60d
            : (args.RenderingTime - _lastFrame).TotalSeconds;
        _lastFrame = args.RenderingTime;

        // Guard against pathological deltas after the app is suspended.
        dt = Math.Clamp(dt, 1d / 240d, 0.25d);
        _elapsed += dt;

        if (!IsActive)
        {
            Array.Fill(_target, 0d);
        }
        else if (LevelSource is not null && LevelSource(_target))
        {
            for (int i = 0; i < BarCount; i++)
                _target[i] = Math.Clamp(_target[i], 0d, 1d);
        }
        else
        {
            UpdateDecorativeTargets();
        }

        bool settled = Advance(dt);
        InvalidateVisual();

        // Once paused and fully at rest there is nothing left to animate.
        if (!IsActive && settled)
            Unsubscribe();
    }

    private void UpdateDecorativeTargets()
    {
        for (int i = 0; i < BarCount; i++)
        {
            double wave = Math.Sin(_elapsed * DecorativeRates[i] + DecorativePhases[i]);
            // Bias upward so bars spend more time in the expressive upper range.
            _target[i] = 0.55d + 0.45d * wave;
        }
    }

    /// <summary>Eases current levels toward their targets. Returns true once at rest.</summary>
    private bool Advance(double dt)
    {
        // Exponential smoothing expressed per-second, so motion is identical
        // regardless of display refresh rate.
        double attack = 1d - Math.Exp(-dt / AttackTau);
        double decay = 1d - Math.Exp(-dt / DecayTau);

        bool settled = true;
        for (int i = 0; i < BarCount; i++)
        {
            double target = _target[i];
            double rate = target > _current[i] ? attack : decay;
            _current[i] += (target - _current[i]) * rate;

            if (Math.Abs(_current[i] - target) > 0.002d)
                settled = false;
        }
        return settled;
    }

    // ---- Drawing ----------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var brush = BarBrush;
        if (brush is null) return;

        // At the default four this is the original 3px bar with a 3px gap.
        var (barWidth, barGap) = BarGeometry(_barCount);

        double centreY = ActualHeight / 2d;
        double radius = barWidth / 2d;   // fully rounded caps

        for (int i = 0; i < _barCount; i++)
        {
            double height = MinBarHeight + _current[i] * (MaxBarHeight - MinBarHeight);
            double x = i * (barWidth + barGap);

            var rect = new Rect(x, centreY - height / 2d, barWidth, height);
            dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
        }
    }
}
