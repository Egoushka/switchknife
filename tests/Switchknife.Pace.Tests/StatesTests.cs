namespace Switchknife.Pace.Tests;

public sealed class StatesTests
{
    [Fact]
    public void A_consumer_inside_its_pace_line_is_ok()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(3.5);
        rig.UseAndRise("a", 10, 0.05);

        Assert.Equal(PaceState.Ok, rig.Consumer("a").State);
        Assert.Equal(PaceState.Ok, rig.Ledger.Status().Account.State);
    }

    [Fact]
    public void A_consumer_past_its_pace_line_is_over_pace()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.UseAndRise("a", 10, 0.08);

        var a = rig.Consumer("a");
        Assert.True(a.Used > a.Allowance);
        Assert.Equal(PaceState.OverPace, a.State);
        Assert.Equal(PaceState.Ok, rig.Consumer("b").State);
    }

    [Fact]
    public void The_account_backstop_makes_every_consumer_over_pace_when_the_meter_is_past_the_total_pace_line()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.Meter(0.30);

        var account = rig.Ledger.Status().Account;
        Assert.Equal(PaceState.OverPace, account.State);
        Assert.Equal(0.9 / 7, account.PaceLine, 9);
        Assert.Equal(PaceState.OverPace, rig.Consumer("a").State);
        Assert.Equal(PaceState.OverPace, rig.Consumer("b").State);
    }

    [Fact]
    public void A_rejected_reading_limits_the_account_until_the_reported_reset()
    {
        var rig = new Rig();
        rig.Meter(0.5);
        rig.Meter(0.6, MeterWindow.FiveHour, rejected: true);

        Assert.Equal(PaceState.Limited, rig.Ledger.Status().Account.State);
        Assert.Equal(PaceState.Limited, rig.Consumer("a").State);

        rig.Clock.Advance(TimeSpan.FromHours(4.9));
        Assert.Equal(PaceState.Limited, rig.Consumer("a").State);

        rig.Clock.Advance(TimeSpan.FromHours(0.2));
        rig.Meter(0.5);
        Assert.NotEqual(PaceState.Limited, rig.Consumer("a").State);
    }

    [Fact]
    public void A_meter_older_than_the_stale_age_is_stale()
    {
        var rig = new Rig();
        rig.Meter(0.0);

        rig.Clock.Advance(TimeSpan.FromHours(5.9));
        Assert.NotEqual(PaceState.Stale, rig.Consumer("a").State);

        rig.Clock.Advance(TimeSpan.FromHours(0.2));
        Assert.Equal(PaceState.Stale, rig.Consumer("a").State);
        Assert.Equal(PaceState.Stale, rig.Ledger.Status().Account.State);

        rig.Meter(0.0);
        Assert.NotEqual(PaceState.Stale, rig.Consumer("a").State);
    }

    [Fact]
    public void Limited_wins_over_stale_and_stale_over_over_pace()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.Meter(0.30);
        Assert.Equal(PaceState.OverPace, rig.Consumer("a").State);

        rig.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(PaceState.Stale, rig.Consumer("a").State);

        rig.Meter(0.35, MeterWindow.FiveHour, rejected: true);
        Assert.Equal(PaceState.Limited, rig.Consumer("a").State);
    }

    [Fact]
    public void Before_any_meter_reading_a_consumer_is_ok_and_admitted()
    {
        var rig = new Rig();

        Assert.Equal(PaceState.Ok, rig.Consumer("a").State);
        Assert.Equal(Decision.Allow, rig.Ledger.Admit(new AdmitRequest("a")).Decision);
    }

    [Fact]
    public void The_answer_follows_the_state_and_the_consumers_settings()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.UseAndRise("a", 10, 0.08);
        var hold = rig.Ledger.Admit(new AdmitRequest("a"));
        Assert.Equal(Decision.Hold, hold.Would);
        Assert.True(hold.RetryAfter > TimeSpan.Zero);

        rig.Meter(0.5, MeterWindow.FiveHour, rejected: true);
        var limited = rig.Ledger.Admit(new AdmitRequest("a"));
        Assert.Equal(Decision.Hold, limited.Would);
        Assert.InRange(limited.RetryAfter!.Value, TimeSpan.FromHours(4.9), TimeSpan.FromHours(5.1));

        var api = rig.Ledger.Admit(new AdmitRequest("b"));
        Assert.Equal(Decision.Hold, api.Would);
    }

    [Fact]
    public void An_over_pace_consumer_with_an_api_action_is_told_to_use_the_api()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.UseAndRise("b", 10, 0.08);

        var admission = rig.Ledger.Admit(new AdmitRequest("b"));
        Assert.Equal(Decision.Api, admission.Would);
        Assert.Null(admission.RetryAfter);
    }

    [Fact]
    public void A_stale_meter_answers_with_the_consumers_on_stale_action()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Clock.Advance(TimeSpan.FromHours(7));

        Assert.Equal(Decision.Hold, rig.Ledger.Admit(new AdmitRequest("a")).Would);
        Assert.Equal(PaceState.Stale, rig.Ledger.Admit(new AdmitRequest("a")).Status.State);
    }

    [Fact]
    public void Retry_after_is_the_time_the_pace_line_needs_to_catch_up_and_never_past_the_reset()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.UseAndRise("a", 10, 0.0875);

        // used 0.0875 against allowance 0.25/7 = 0.0357; excess 0.0518 → 0.0518/0.25 weeks.
        var retry = rig.Ledger.Admit(new AdmitRequest("a")).RetryAfter!.Value;
        Assert.InRange(retry.TotalDays, 7 * (0.0875 - (0.25 / 7)) / 0.25 - 0.01, (7 * (0.0875 - (0.25 / 7)) / 0.25) + 0.01);
        Assert.True(retry <= Rig.Reset - rig.Clock.GetUtcNow());
    }
}
