using Barline.Media;
using Xunit;

namespace Barline.Tests;

/// <summary>
/// The playback clock. Everything time-synced the widget will ever do rests on this
/// being right, and it is the one part that can be checked exactly: the class reads
/// no system clock, so a test can hand it any instant it likes.
/// </summary>
public class PlaybackClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Length = TimeSpan.FromMinutes(4);

    private static PlaybackAnchor Report(
        double positionSeconds,
        double reportedSecondsAfterT0 = 0d,
        bool isPlaying = true,
        double rate = 1d,
        TimeSpan? duration = null) =>
        new(
            Position: TimeSpan.FromSeconds(positionSeconds),
            Duration: duration ?? Length,
            Rate: rate,
            IsPlaying: isPlaying,
            ReportedAt: T0.AddSeconds(reportedSecondsAfterT0));

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>
    /// Positions are compared with a tolerance throughout: these are floating-point
    /// extrapolations, and asserting exact ticks would test the arithmetic rather
    /// than the behavior.
    /// </summary>
    private static void AssertClose(TimeSpan expected, TimeSpan actual, TimeSpan tolerance)
    {
        var drift = (actual - expected).Duration();

        Assert.True(
            drift <= tolerance,
            $"expected {expected.TotalMilliseconds:F1}ms but got {actual.TotalMilliseconds:F1}ms " +
            $"— off by {drift.TotalMilliseconds:F1}ms, tolerance {tolerance.TotalMilliseconds:F1}ms");
    }

    // ---- Extrapolation -----------------------------------------------------

    [Fact]
    public void Position_advances_between_reports()
    {
        var clock = new PlaybackClock();
        Assert.True(clock.Anchor(Report(30d), At(0)));

        // No further reports; the whole point is that it keeps running anyway.
        AssertClose(TimeSpan.FromSeconds(35), clock.PositionAt(At(5)), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void A_paused_session_holds_its_position()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d, isPlaying: false), At(0));

        AssertClose(TimeSpan.FromSeconds(30), clock.PositionAt(At(60)), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Playback_rate_is_honored()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d, rate: 1.5d), At(0));

        AssertClose(TimeSpan.FromSeconds(45), clock.PositionAt(At(10)), TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// The report's own timestamp is the anchor, not the moment we read it. A report
    /// that is already a few seconds old describes the past, and the position has to
    /// be carried forward from then rather than from now.
    /// </summary>
    [Fact]
    public void A_stale_report_is_carried_forward_from_when_it_was_made()
    {
        var clock = new PlaybackClock();

        // Reported at T0+0 saying "30s", but only reaching us at T0+4.
        clock.Anchor(Report(30d, reportedSecondsAfterT0: 0d), At(4));

        AssertClose(TimeSpan.FromSeconds(34), clock.PositionAt(At(4)), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Position_never_runs_past_the_end_of_the_track()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(Length.TotalSeconds - 2d), At(0));

        Assert.Equal(Length, clock.PositionAt(At(600)));
    }

    // ---- Corrections -------------------------------------------------------

    /// <summary>
    /// The behavior the class exists for. Ordinary drift must not step the position,
    /// because anything driven by this clock would visibly twitch every time a source
    /// app published a report.
    /// </summary>
    [Fact]
    public void Small_drift_is_eased_in_rather_than_stepped()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d), At(0));

        var before = clock.PositionAt(At(5));

        // A report 100ms behind where we had estimated.
        clock.Anchor(Report(34.9d, reportedSecondsAfterT0: 5d), At(5));

        var immediately = clock.PositionAt(At(5));
        Assert.True(
            (immediately - before).Duration() < TimeSpan.FromMilliseconds(10),
            $"position stepped by {(immediately - before).TotalMilliseconds:F0}ms on a small correction");

        // But it does converge, rather than ignoring the correction outright.
        AssertClose(TimeSpan.FromSeconds(35.9), clock.PositionAt(At(6)), TimeSpan.FromMilliseconds(15));
    }

    /// <summary>A seek is not drift and must land at once.</summary>
    [Fact]
    public void A_seek_is_applied_immediately()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d), At(0));
        clock.PositionAt(At(5));

        clock.Anchor(Report(150d, reportedSecondsAfterT0: 5d), At(5));

        AssertClose(TimeSpan.FromSeconds(150), clock.PositionAt(At(5)), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void A_correction_below_the_threshold_still_converges_on_the_reported_position()
    {
        foreach (double error in new[] { -0.24d, -0.05d, 0.05d, 0.24d })
        {
            var clock = new PlaybackClock();
            clock.Anchor(Report(30d), At(0));
            clock.PositionAt(At(5));

            clock.Anchor(Report(35d + error, reportedSecondsAfterT0: 5d), At(5));

            // Three tau absorbs ~95%; one second is comfortably past that.
            AssertClose(
                TimeSpan.FromSeconds(36d + error),
                clock.PositionAt(At(6)),
                TimeSpan.FromMilliseconds(15));
        }
    }

    /// <summary>
    /// The eased correction must depend on elapsed time alone. Decaying per call
    /// would make the position depend on the display's refresh rate.
    /// </summary>
    [Fact]
    public void Polling_more_often_does_not_change_the_result()
    {
        var sparse = new PlaybackClock();
        var frequent = new PlaybackClock();

        foreach (var clock in new[] { sparse, frequent })
        {
            clock.Anchor(Report(30d), At(0));
            clock.PositionAt(At(5));
            clock.Anchor(Report(34.9d, reportedSecondsAfterT0: 5d), At(5));
        }

        for (double t = 5d; t < 6d; t += 0.01d)
            frequent.PositionAt(At(t));

        AssertClose(sparse.PositionAt(At(6)), frequent.PositionAt(At(6)), TimeSpan.FromTicks(1));
    }

    // ---- Rejecting unusable reports ----------------------------------------

    [Theory]
    [InlineData(0d, "no duration at all")]
    [InlineData(-1d, "negative duration")]
    public void A_session_with_no_timeline_is_not_usable(double durationSeconds, string why)
    {
        var clock = new PlaybackClock();

        bool accepted = clock.Anchor(
            Report(0d, duration: TimeSpan.FromSeconds(durationSeconds)), At(0));

        Assert.False(accepted, why);
        Assert.False(clock.IsUsable, why);
    }

    [Fact]
    public void A_report_timestamped_at_the_epoch_is_rejected()
    {
        var clock = new PlaybackClock();

        var garbage = new PlaybackAnchor(
            Position: TimeSpan.FromSeconds(10),
            Duration: Length,
            Rate: 1d,
            IsPlaying: true,
            ReportedAt: default);

        Assert.False(clock.Anchor(garbage, At(0)));
        Assert.False(clock.IsUsable);
    }

    [Fact]
    public void A_position_beyond_the_track_is_rejected()
    {
        var clock = new PlaybackClock();

        Assert.False(clock.Anchor(Report(Length.TotalSeconds + 30d), At(0)));
    }

    /// <summary>A rejected report must not disturb a good anchor already in place.</summary>
    [Fact]
    public void A_rejected_report_leaves_the_previous_estimate_intact()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d), At(0));

        clock.Anchor(Report(10d, duration: TimeSpan.Zero), At(5));

        Assert.True(clock.IsUsable);
        AssertClose(TimeSpan.FromSeconds(35), clock.PositionAt(At(5)), TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// Some sources report themselves as playing while leaving the rate at zero.
    /// Taken literally the position would never move.
    /// </summary>
    [Fact]
    public void A_zero_rate_while_playing_is_treated_as_normal_speed()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d, rate: 0d), At(0));

        AssertClose(TimeSpan.FromSeconds(35), clock.PositionAt(At(5)), TimeSpan.FromMilliseconds(1));
    }

    // ---- Diagnostics -------------------------------------------------------

    /// <summary>
    /// Each report doubles as a measurement of the extrapolation before it, which is
    /// how the clock is judged against real players without instrumenting them.
    /// </summary>
    [Fact]
    public void Each_report_measures_the_extrapolation_that_preceded_it()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d), At(0));

        // We would have predicted 35.0s; the source says it was really at 34.8s.
        clock.Anchor(Report(34.8d, reportedSecondsAfterT0: 5d), At(5));

        Assert.NotNull(clock.LastPredictionError);
        AssertClose(
            TimeSpan.FromMilliseconds(200),
            clock.LastPredictionError!.Value,
            TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void The_first_report_has_nothing_to_measure_against()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d), At(0));

        Assert.Null(clock.LastPredictionError);
    }

    /// <summary>
    /// A new track shares no timeline with the old one, so comparing them would
    /// report a huge error that means nothing and would pollute the diagnostic.
    /// </summary>
    [Fact]
    public void A_track_change_is_not_reported_as_a_prediction_error()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(200d), At(0));

        clock.Anchor(
            Report(0d, reportedSecondsAfterT0: 5d, duration: TimeSpan.FromMinutes(3)),
            At(5));

        Assert.Null(clock.LastPredictionError);
        AssertClose(TimeSpan.Zero, clock.PositionAt(At(5)), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Resetting_forgets_the_session()
    {
        var clock = new PlaybackClock();
        clock.Anchor(Report(30d), At(0));

        clock.Reset();

        Assert.False(clock.IsUsable);
        Assert.Equal(TimeSpan.Zero, clock.PositionAt(At(5)));
        Assert.Equal(TimeSpan.Zero, clock.Duration);
    }
}
