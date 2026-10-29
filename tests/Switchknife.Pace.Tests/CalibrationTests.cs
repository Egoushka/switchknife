namespace Switchknife.Pace.Tests;

public sealed class CalibrationTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Feeds one interval: <paramref name="usd"/> reported by a, then the meter at <paramref name="utilization"/>.</summary>
    private static void Interval(Rig rig, double usd, double utilization)
    {
        rig.Use("a", usd);
        rig.Meter(utilization);
    }

    [Fact]
    public void CalibrateQuietHoursOnly_rises_outside_quiet_hours_do_not_change_k()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Clock.Advance(9 * Hour);
        rig.Meter(0.0);
        Assert.Null(rig.Ledger.Status().Account.K);

        Interval(rig, 10, 0.05);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.12);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.17);

        Assert.Null(rig.Ledger.Status().Account.K);
    }

    [Fact]
    public void CalibrateQuietHoursOnly_rises_inside_quiet_hours_set_k()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Clock.Advance(Hour);
        rig.Meter(0.0);

        Interval(rig, 10, 0.05);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.10);

        Assert.Equal(0.005, rig.Ledger.Status().Account.K!.Value, 9);
    }

    [Fact]
    public void An_interval_that_starts_in_the_day_and_ends_at_night_is_not_used()
    {
        var rig = new Rig();
        rig.Clock.Advance(23 * Hour);
        rig.Meter(0.0);
        rig.Use("a", 10);
        rig.Clock.Advance(2 * Hour);
        rig.Meter(0.30);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.35);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.40);

        Assert.Equal(0.005, rig.Ledger.Status().Account.K!.Value, 9);
    }

    [Fact]
    public void Quiet_hours_may_cross_midnight()
    {
        var quiet = new QuietHours(new TimeOnly(22, 0), new TimeOnly(6, 0), TimeZoneInfo.Utc);

        Assert.True(quiet.Contains(new DateTimeOffset(2026, 11, 2, 23, 0, 0, TimeSpan.Zero)));
        Assert.True(quiet.Contains(new DateTimeOffset(2026, 11, 3, 5, 59, 0, TimeSpan.Zero)));
        Assert.False(quiet.Contains(new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.Zero)));
        Assert.True(quiet.ContainsInterval(new DateTimeOffset(2026, 11, 2, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Use_by_a_reporter_the_meter_does_not_match_switches_used_to_the_calibrated_estimate()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Clock.Advance(Hour);
        rig.Meter(0.0);
        Interval(rig, 10, 0.05);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.10);

        // Daytime: a reports $10 (worth 0.05) but the owner's unreported use moves the meter by 0.15 more: 0.30 in all, 0.15 of it a's.
        rig.Clock.Advance(10 * Hour);
        Interval(rig, 10, 0.30);

        var a = rig.Consumer("a");
        Assert.Equal(Estimator.Calibrated, a.Estimator);
        Assert.Equal(0.15, a.Used, 9);
        Assert.Equal(0.15, a.Calibrated!.Value, 9);
        Assert.True(a.Apportioned > a.Used);
        Assert.Equal(0.15, rig.Ledger.Status().Account.Untracked, 9);
    }

    [Fact]
    public void When_the_meter_matches_the_reported_use_the_apportioned_estimate_stands()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Clock.Advance(Hour);
        rig.Meter(0.0);
        Interval(rig, 10, 0.05);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.10);
        rig.Clock.Advance(10 * Hour);
        Interval(rig, 10, 0.15);

        var a = rig.Consumer("a");
        Assert.Equal(Estimator.Apportion, a.Estimator);
        Assert.Equal(0.15, a.Used, 9);
    }

    [Fact]
    public void A_finished_weeks_k_carries_into_the_next_until_it_has_its_own()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Clock.Advance(Hour);
        rig.Meter(0.0);
        Interval(rig, 10, 0.05);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.10);

        rig.AdvanceDays(7);
        rig.Ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.0, Rig.Reset.AddDays(7)));

        Assert.Equal(0.005, rig.Ledger.Status().Account.K!.Value, 9);
        Assert.Equal(0.005, rig.Ledger.Snapshot().PriorK!.Value, 9);
    }

    [Fact]
    public void A_reservation_uses_k_once_it_is_known()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Clock.Advance(Hour);
        rig.Meter(0.0);
        Interval(rig, 10, 0.05);
        rig.Clock.Advance(Hour);
        Interval(rig, 10, 0.10);

        rig.Ledger.Admit(new AdmitRequest("a", 4));

        Assert.Equal(0.02, rig.Consumer("a").Reserved, 9);
    }

    [Fact]
    public void Quiet_hours_with_equal_start_and_end_are_invalid()
    {
        var policy = new Policy
        {
            Consumers = new Dictionary<string, ConsumerPolicy> { ["a"] = new(0.5) },
            Quiet = new QuietHours(new TimeOnly(3, 0), new TimeOnly(3, 0), TimeZoneInfo.Utc),
        };

        Assert.Single(policy.Validate());
    }
}
