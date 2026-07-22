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
    private const double MinBarHeight = 5d;
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
    private bool _hasExternalLevels;

    public Visualizer()
    {
        Width = BarCount * BarWidth + (BarCount - 1) * BarGap;   // 21px
        Height = MaxBarHeight;
        IsHitTestVisible = false;

        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Unsubscribe();
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
    /// Supplies externally computed band levels (0..1). Called by the audio
    /// pipeline in Phase 4; until then the decorative motion drives the bars.
    /// </summary>
    public void SetLevels(ReadOnlySpan<double> levels)
    {
        _hasExternalLevels = true;
        int n = Math.Min(levels.Length, BarCount);
        for (int i = 0; i < n; i++)
            _target[i] = Math.Clamp(levels[i], 0d, 1d);
    }

    /// <summary>Reverts to the decorative loop, e.g. if audio capture drops out.</summary>
    public void ClearExternalLevels() => _hasExternalLevels = false;

    // ---- Frame loop -------------------------------------------------------

    private void UpdateSubscription()
    {
        if (IsActive && IsLoaded) Subscribe();
        // When inactive we keep rendering until the bars have settled, then stop.
        else if (!IsActive) Subscribe();
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

        // WPF can raise Rendering more than once for the same frame.
        if (args.RenderingTime == _lastFrame) return;

        double dt = _lastFrame == TimeSpan.Zero
            ? 1d / 60d
            : (args.RenderingTime - _lastFrame).TotalSeconds;
        _lastFrame = args.RenderingTime;

        // Guard against pathological deltas after the app is suspended.
        dt = Math.Clamp(dt, 1d / 240d, 0.25d);
        _elapsed += dt;

        if (IsActive && !_hasExternalLevels)
            UpdateDecorativeTargets();
        else if (!IsActive)
            Array.Fill(_target, 0d);

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
