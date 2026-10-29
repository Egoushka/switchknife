namespace Switchknife.Pace.Tests;

public sealed class WeekRolloverTests
{
    [Fact]
    public void A_later_weekly_reset_starts_new_counters_and_keeps_the_old_week()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(3);
        rig.UseAndRise("a", 10, 0.06);
        Assert.Equal(0.06, rig.Consumer("a").Used, 9);

        rig.AdvanceDays(4.5);
        rig.Ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.01, Rig.Reset.AddDays(7)));

        Assert.Equal(0, rig.Consumer("a").Used);
        Assert.Equal(0, rig.Consumer("a").WeightedUsd);
        Assert.Equal(0.01, rig.Ledger.Status().Account.Utilization);
        var old = Assert.Single(rig.Ledger.Snapshot().History);
        Assert.Equal(Rig.Reset, old.ResetsAt);
        Assert.Equal(0.06, old.Used["a"], 9);
        Assert.Contains(rig.Ledger.DrainEvents(), e => e.Kind == "week_started");
    }

    [Fact]
    public void The_new_weeks_pace_line_starts_at_zero()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(7.5);
        rig.Ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.0, Rig.Reset.AddDays(7)));

        Assert.Equal(0.25 * (0.5 / 7), rig.Consumer("a").Allowance, 9);
    }

    [Fact]
    public void A_late_reading_of_the_old_week_changes_nothing()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(7.5);
        rig.Ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.02, Rig.Reset.AddDays(7)));
        rig.Ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.99, Rig.Reset));

        Assert.Equal(0.02, rig.Ledger.Status().Account.Utilization);
    }

    [Fact]
    public void Only_eight_finished_weeks_are_kept()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        for (var week = 1; week <= 10; week++)
        {
            rig.AdvanceDays(7);
            rig.Ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.0, Rig.Reset.AddDays(7 * week)));
        }

        Assert.Equal(Ledger.HistoryWeeks, rig.Ledger.Snapshot().History.Count);
    }

    [Fact]
    public void Use_reported_before_the_first_weekly_reading_waits_for_it()
    {
        var rig = new Rig();
        rig.Use("a", 4);
        rig.Meter(0.10);
        rig.Use("a", 1);
        rig.Meter(0.15);

        Assert.Equal(5, rig.Consumer("a").WeightedUsd);
        Assert.Equal(0.05, rig.Consumer("a").Used, 9);
    }
}
