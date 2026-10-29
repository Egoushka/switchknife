namespace Switchknife.Pace.Tests;

public sealed class PaceLineTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(3.5, 0.5)]
    [InlineData(7.0, 1.0)]
    public void Allowance_is_the_share_times_the_elapsed_part_of_the_week(double days, double expectedPartOfShare)
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(days);
        rig.Meter(0.0);

        Assert.Equal(0.25 * expectedPartOfShare, rig.Consumer("a").Allowance, 9);
        Assert.Equal(0.10 * expectedPartOfShare, rig.Consumer("b").Allowance, 9);
    }

    [Fact]
    public void Unused_allowance_carries_forward()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.UseAndRise("a", 10, 0.02);
        rig.AdvanceDays(2.5);
        rig.Meter(0.02);

        var a = rig.Consumer("a");
        Assert.Equal(0.125, a.Allowance, 9);
        Assert.Equal(0.02, a.Used, 9);
        Assert.Equal(0.105, a.Remaining, 9);
    }

    [Fact]
    public void The_week_is_over_after_its_reset_and_the_pace_line_stops_at_the_share()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(9);

        Assert.Equal(0.25, rig.Consumer("a").Allowance, 9);
    }

    [Fact]
    public void Without_a_weekly_reading_there_is_no_pace_line_and_the_state_is_ok()
    {
        var rig = new Rig();
        rig.Use("a", 5);

        var a = rig.Consumer("a");
        Assert.Equal(0, a.Allowance);
        Assert.Equal(PaceState.Ok, a.State);
        Assert.Equal(5, a.WeightedUsd);
        Assert.Equal(Decision.Allow, rig.Ledger.Admit(new AdmitRequest("a")).Would);
    }

    [Fact]
    public void A_meter_rise_is_split_over_the_use_reported_since_the_last_rise()
    {
        var rig = new Rig();
        rig.Meter(0.10);
        rig.Use("a", 6);
        rig.Use("b", 2);
        rig.Meter(0.18);

        Assert.Equal(0.06, rig.Consumer("a").Used, 9);
        Assert.Equal(0.02, rig.Consumer("b").Used, 9);
        Assert.Equal(0, rig.Ledger.Status().Account.Untracked, 9);
    }

    [Fact]
    public void A_rise_with_no_reported_use_is_untracked()
    {
        var rig = new Rig();
        rig.Meter(0.10);
        rig.Meter(0.13);

        Assert.Equal(0.03, rig.Ledger.Status().Account.Untracked, 9);
        Assert.Equal(0, rig.Consumer("a").Used);
    }

    [Fact]
    public void Use_counts_toward_the_rise_that_follows_even_when_several_readings_show_no_change()
    {
        var rig = new Rig();
        rig.Meter(0.10);
        rig.Use("a", 1);
        rig.Meter(0.10);
        rig.Use("b", 1);
        rig.Meter(0.10);
        rig.Meter(0.12);

        Assert.Equal(0.01, rig.Consumer("a").Used, 9);
        Assert.Equal(0.01, rig.Consumer("b").Used, 9);
    }

    [Fact]
    public void A_lower_reading_never_lowers_the_week()
    {
        var rig = new Rig();
        rig.Meter(0.20);
        rig.Meter(0.19);

        Assert.Equal(0.20, rig.Ledger.Status().Account.Utilization);
    }

    [Fact]
    public void Resets_that_differ_by_seconds_are_one_week()
    {
        var rig = new Rig();
        rig.Meter(0.10);
        rig.Use("a", 1);
        rig.Meter(0.12, resetsAt: Rig.Reset.AddSeconds(3));

        Assert.Equal(0.02, rig.Consumer("a").Used, 9);
        Assert.Empty(rig.Ledger.Snapshot().History);
    }
}
