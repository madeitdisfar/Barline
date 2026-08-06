namespace Barline.Media;

/// <summary>
/// A position report from the media session, as SMTC gave it to us.
/// </summary>
/// <remarks>
/// <see cref="ReportedAt"/> is the source app's own timestamp, not when we read it.
/// Using our read time instead would fold the delay between the app publishing and
/// the event reaching us into the position, which is exactly the error the clock
/// exists to avoid.
/// </remarks>
internal readonly record struct PlaybackAnchor(
    TimeSpan Position,
    TimeSpan Duration,
    double Rate,
    bool IsPlaying,
    DateTimeOffset ReportedAt);

/// <summary>
/// Estimates the current playback position continuously, from the sparse and
/// irregular position reports SMTC actually provides.
/// </summary>
/// <remarks>
/// <para>
/// SMTC does not tick. A source app publishes a position when it feels like it —
/// often seconds apart, sometimes only on seek — so anything that needs to know
/// where playback is *right now* has to extrapolate between reports and re-anchor
/// when the next one lands.
/// </para>
/// <para>
/// Corrections are eased in rather than applied outright. A report that disagrees
/// with the estimate by a few tens of milliseconds is ordinary drift, and snapping
/// to it would make anything driven by this clock visibly twitch several times a
/// minute. A large disagreement is a seek, which must be instant — so the two are
/// separated by <see cref="SnapThreshold"/> rather than smoothed alike.
/// </para>
/// <para>
/// Every method takes the current instant rather than reading the system clock, so
/// the whole class is a pure function of its inputs and can be tested exactly.
/// </para>
/// </remarks>
internal sealed class PlaybackClock
{
    /// <summary>
    /// Disagreement above which a report is treated as a seek and applied outright.
    /// Below it, the correction is eased in.
    /// </summary>
    /// <remarks>
    /// A quarter second is far more than observed drift between reports but far less
    /// than any deliberate seek, so the two do not overlap in practice.
    /// </remarks>
    public static readonly TimeSpan SnapThreshold = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Time constant for easing a correction in. Roughly 95% of it is absorbed within
    /// three tau, so a correction lands in under half a second.
    /// </summary>
    private const double SlewTau = 0.15d;

    /// <summary>
    /// Reports timestamped before this are nonsense rather than merely stale — some
    /// sources publish a default or zeroed value when they have no real position.
    /// </summary>
    private static readonly DateTimeOffset TimestampFloor = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Tolerance for a report timestamped slightly in the future.</summary>
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();

    private PlaybackAnchor? _anchor;
    private double _slewSeconds;
    private DateTimeOffset _slewFrom;

    /// <summary>
    /// Whether the session reports a position worth trusting. False for sources that
    /// publish no timeline at all — live streams, and some browser sessions — which
    /// must fall back to unsynced behaviour rather than showing a confident guess.
    /// </summary>
    public bool IsUsable
    {
        get { lock (_gate) return _anchor is not null; }
    }

    /// <summary>Track length, or zero when unknown.</summary>
    public TimeSpan Duration
    {
        get { lock (_gate) return _anchor?.Duration ?? TimeSpan.Zero; }
    }

    /// <summary>
    /// How far the previous estimate was out when the latest report arrived, or null
    /// if there was nothing to compare against.
    /// </summary>
    /// <remarks>
    /// Each report doubles as a free measurement of the extrapolation that preceded
    /// it, which is the only honest way to judge the clock without instrumenting the
    /// source app. Consumed by the diagnostics log.
    /// </remarks>
    public TimeSpan? LastPredictionError { get; private set; }

    /// <summary>
    /// Accepts a position report. Returns false when the report is unusable, in which
    /// case the clock is left as it was.
    /// </summary>
    public bool Anchor(PlaybackAnchor anchor, DateTimeOffset now)
    {
        if (!IsSane(anchor, now)) return false;

        anchor = Normalize(anchor);

        lock (_gate)
        {
            // A different length is a different track, and nothing about the old
            // anchor carries over: the correction would be meaningless and the error
            // measurement would be comparing two unrelated timelines. Same-length
            // tracks slip through, but the disagreement is then large enough that the
            // snap below handles it anyway.
            bool sameTrack = _anchor is { } existing && existing.Duration == anchor.Duration;

            if (sameTrack && _anchor is { } previous)
            {
                // Compare at the same instant the new report describes, so the error
                // measures the extrapolation rather than the gap between readings.
                var predicted = ExtrapolateRaw(previous, anchor.ReportedAt);
                LastPredictionError = predicted - anchor.Position;

                // Carry the currently displayed position forward, so an ordinary
                // correction is absorbed instead of stepping.
                var shown = PositionAtLocked(now);
                var corrected = ExtrapolateRaw(anchor, now);
                var delta = shown - corrected;

                _slewSeconds = delta.Duration() > SnapThreshold ? 0d : delta.TotalSeconds;
            }
            else
            {
                LastPredictionError = null;
                _slewSeconds = 0d;
            }

            _slewFrom = now;
            _anchor = anchor;
        }

        return true;
    }

    /// <summary>Best estimate of the current position.</summary>
    public TimeSpan PositionAt(DateTimeOffset now)
    {
        lock (_gate) return PositionAtLocked(now);
    }

    /// <summary>Forgets everything. Called when the track changes.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _anchor = null;
            _slewSeconds = 0d;
            LastPredictionError = null;
        }
    }

    private TimeSpan PositionAtLocked(DateTimeOffset now)
    {
        if (_anchor is not { } anchor) return TimeSpan.Zero;

        var raw = ExtrapolateRaw(anchor, now);

        // Held as a decaying amount rather than stepped down per call, so the result
        // depends only on elapsed time and not on how often the clock is asked.
        double age = Math.Max(0d, (now - _slewFrom).TotalSeconds);
        double offset = _slewSeconds * Math.Exp(-age / SlewTau);

        return Clamp(raw + TimeSpan.FromSeconds(offset), anchor.Duration);
    }

    private static TimeSpan ExtrapolateRaw(PlaybackAnchor anchor, DateTimeOffset at)
    {
        if (!anchor.IsPlaying) return Clamp(anchor.Position, anchor.Duration);

        double elapsed = (at - anchor.ReportedAt).TotalSeconds * anchor.Rate;

        // A report from the future would otherwise wind the position backwards.
        if (elapsed < 0d) elapsed = 0d;

        return Clamp(anchor.Position + TimeSpan.FromSeconds(elapsed), anchor.Duration);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        return value > duration ? duration : value;
    }

    /// <summary>
    /// Rejects reports that cannot describe real playback.
    /// </summary>
    /// <remarks>
    /// A stale timestamp is not a failure — that is the normal case this class exists
    /// to handle. What is rejected is a missing duration, a position outside the
    /// track, or a timestamp that is not a real point in time.
    /// </remarks>
    private static bool IsSane(PlaybackAnchor anchor, DateTimeOffset now)
    {
        if (anchor.Duration <= TimeSpan.Zero) return false;
        if (anchor.Position < TimeSpan.Zero) return false;
        if (anchor.Position > anchor.Duration + ClockSkewAllowance) return false;
        if (anchor.ReportedAt < TimestampFloor) return false;
        if (anchor.ReportedAt > now + ClockSkewAllowance) return false;

        return true;
    }

    /// <summary>
    /// Repairs a report that is usable but self-inconsistent.
    /// </summary>
    /// <remarks>
    /// Some sources leave the rate at zero while reporting themselves as playing.
    /// Taken literally the position would never advance, so the playback status wins
    /// and the rate falls back to normal speed.
    /// </remarks>
    private static PlaybackAnchor Normalize(PlaybackAnchor anchor) =>
        anchor.IsPlaying && anchor.Rate <= 0d
            ? anchor with { Rate = 1d }
            : anchor;
}
