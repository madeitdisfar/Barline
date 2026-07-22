using System.Windows;
using System.Windows.Media;

namespace TaskbarMusicWidget.Ui;

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
    private const int BarCount = 4;
    private const double BarWidth = 3d;
    private const double BarGap = 3d;

    /// <summary>
    /// Resting height. Must stay comfortably above <see cref="BarWidth"/>: with fully
    /// rounded caps, a bar shorter than it is wide stops reading as a bar and turns
    /// into a dot.
    /// </summary>
    private const double MinBarHeight = 6d;
    private const double MaxBarHeight = 18d;

    // Time constants, in seconds. Attack is quick enough to catch transients;
    // decay is slow enough that the motion reads as fluid rather than twitchy.
    private const double AttackTau = 0.035d;
    private const double DecayTau = 0.220d;

    private readonly double[] _current = new double[BarCount];
    private readonly double[] _target = new double[BarCount];

    // Deliberately irrational-ish ratios so the decorative loop never lines up
    // into an obvious repeating pattern.
    private static readonly double[] DecorativeRates = [2.9d, 3.7d, 2.3d, 3.1d];
    private static readonly double[] DecorativePhases = [0.0d, 1.7d, 3.4d, 5.1d];

    private TimeSpan _lastFrame;
    private double _elapsed;
    private bool _subscribed;

    public Visualizer()
    {
        Width = BarCount * BarWidth + (BarCount - 1) * BarGap;   // 21px
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

        double centreY = ActualHeight / 2d;
        double radius = BarWidth / 2d;   // fully rounded caps

        for (int i = 0; i < BarCount; i++)
        {
            double height = MinBarHeight + _current[i] * (MaxBarHeight - MinBarHeight);
            double x = i * (BarWidth + BarGap);

            var rect = new Rect(x, centreY - height / 2d, BarWidth, height);
            dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
        }
    }
}
