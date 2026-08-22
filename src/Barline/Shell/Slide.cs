using System.Windows;
using System.Windows.Media.Animation;
using Barline.Ui;
using static Barline.Shell.NativeMethods;

namespace Barline.Shell;

/// <summary>
/// Moves a window sideways over a quarter of a second instead of in one jump.
/// </summary>
/// <remarks>
/// <para>
/// The widget's own end of the taskbar is not fixed: the tray grows and shrinks as
/// icons and the language indicator come and go, and aligning the taskbar left sends
/// the widget across to the other end entirely. Arriving there instantly reads as a
/// glitch rather than a move, and gives no sense that it is the same widget.
/// </para>
/// <para>
/// The window is what moves, not what is drawn inside it. A render transform would
/// slide the content out of a window that stayed where it was, and the widget paints
/// to its edges, so the content would be clipped by the frame it was leaving. That
/// means animating a <c>SetWindowPos</c>, which is why this is a <see cref="Freezable"/>
/// holding one property rather than anything on a visual.
/// </para>
/// <para>
/// The curve and the duration are the shared Fluent ones, so this reads as the same
/// app as every other animation in the widget.
/// </para>
/// </remarks>
internal sealed class Slide : Animatable
{
    private static readonly DependencyProperty XProperty =
        DependencyProperty.Register(
            "X",
            typeof(double),
            typeof(Slide),
            new PropertyMetadata(0d, (d, e) => ((Slide)d).Moved((double)e.NewValue)));

    private IntPtr _hwnd;
    private int _y;

    /// <summary>
    /// Starts a move, replacing one already running.
    /// </summary>
    /// <param name="hwnd">The window to move.</param>
    /// <param name="to">Where it should end up, in physical pixels.</param>
    /// <param name="y">Its top, which a sideways move leaves alone.</param>
    public void Run(IntPtr hwnd, int to, int y)
    {
        _hwnd = hwnd;
        _y = y;

        // Where the window actually is, rather than where the last move was headed. A
        // second move can start while the first is still part way across, and starting
        // from its destination would snap the window forward before setting off.
        if (!GetWindowRect(hwnd, out var now))
        {
            Moved(to);
            return;
        }

        var move = new DoubleAnimationUsingKeyFrames();

        move.KeyFrames.Add(new LinearDoubleKeyFrame(now.Left, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        move.KeyFrames.Add(new SplineDoubleKeyFrame(
            to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Motion.NormalMs)),
            Motion.Standard));

        // Held at the end rather than released, so the last frame is not undone by the
        // property reverting to its unanimated value.
        move.FillBehavior = FillBehavior.HoldEnd;

        BeginAnimation(XProperty, move);
    }

    /// <summary>
    /// Drops any move in progress, for a caller about to place the window itself.
    /// </summary>
    public void Stop() => BeginAnimation(XProperty, null);

    protected override Freezable CreateInstanceCore() => new Slide();

    private void Moved(double x)
    {
        if (_hwnd == IntPtr.Zero) return;

        // Size and z-order are the placement's business. This asks for a position and
        // nothing else, which is all a window being carried across the screen needs.
        SetWindowPos(
            _hwnd, HWND_TOPMOST, (int)Math.Round(x), _y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
