using System.Windows.Media.Animation;

namespace Barline.Ui;

/// <summary>
/// Shared Fluent motion values, so every animation in the widget uses the same curve
/// and the same two durations Windows itself does.
/// </summary>
/// <remarks>
/// Kept in one place because a second copy of the easing curve is the kind of detail
/// that drifts silently: two animations at slightly different curves read as two
/// different apps overlapping.
/// </remarks>
internal static class Motion
{
    /// <summary>Fluent "fast" — used for hover, where the response must feel instant.</summary>
    public const int FastMs = 150;

    /// <summary>Fluent "normal" — used for content changes like a new track's colour.</summary>
    public const int NormalMs = 250;

    /// <summary>The Fluent standard easing curve, cubic-bezier(0.33, 0, 0.67, 1).</summary>
    public static readonly KeySpline Standard = CreateStandard();

    private static KeySpline CreateStandard()
    {
        var spline = new KeySpline(0.33d, 0.0d, 0.67d, 1.0d);
        spline.Freeze();
        return spline;
    }
}
