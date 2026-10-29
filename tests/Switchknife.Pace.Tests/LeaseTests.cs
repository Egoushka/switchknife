namespace Switchknife.Pace.Tests;

public sealed class LeaseTests
{
    private static Rig Calibrated()
    {
        // $10 of use moved the meter 0.05: half a point of the week per dollar.
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(2);
        rig.UseAndRise("a", 10, 0.05);
        return rig;
    }

    [Fact]
    public void An_open_lease_counts_toward_used_by_its_estimate()
    {
        var rig = Calibrated();
        var before = rig.Consumer("a");

        var admission = rig.Ledger.Admit(new AdmitRequest("a", EstimateUsd: 4));

        Assert.NotNull(admission.LeaseId);
        var during = rig.Consumer("a");
        Assert.Equal(1, during.OpenLeases);
        Assert.Equal(0.02, during.Reserved, 9);
        Assert.Equal(before.Remaining - 0.02, during.Remaining, 9);
    }

    [Fact]
    public void Ten_jobs_admitted_at_once_cannot_all_fit_the_remaining_share()
    {
        var rig = Calibrated();
        var remaining = rig.Consumer("a").Remaining;
        var jobs = (int)Math.Ceiling(remaining / 0.02) + 1;

        var wouldHold = 0;
        for (var i = 0; i < jobs; i++)
        {
            if (rig.Ledger.Admit(new AdmitRequest("a", EstimateUsd: 4)).Would == Decision.Hold)
            {
                wouldHold++;
            }
        }

        Assert.True(wouldHold > 0);
    }

    [Fact]
    public void A_report_with_the_lease_closes_it_and_counts_the_actual_use()
    {
        var rig = Calibrated();
        var lease = rig.Ledger.Admit(new AdmitRequest("a", EstimateUsd: 4)).LeaseId;

        var result = rig.Use("a", 1, lease);

        Assert.True(result.LeaseClosed);
        Assert.Equal(0, rig.Consumer("a").OpenLeases);
        Assert.Equal(0, rig.Consumer("a").Reserved);
        Assert.Equal(11, rig.Consumer("a").WeightedUsd);
    }

    [Fact]
    public void A_report_naming_another_consumers_lease_does_not_close_it()
    {
        var rig = Calibrated();
        var lease = rig.Ledger.Admit(new AdmitRequest("a", EstimateUsd: 4)).LeaseId;

        Assert.False(rig.Use("b", 1, lease).LeaseClosed);
        Assert.Equal(1, rig.Consumer("a").OpenLeases);
    }

    [Fact]
    public void An_expired_lease_releases_its_reservation_and_is_logged_as_unreported()
    {
        var rig = Calibrated();
        var lease = rig.Ledger.Admit(new AdmitRequest("a", EstimateUsd: 4)).LeaseId;
        rig.Clock.Advance(TimeSpan.FromHours(1.9));
        Assert.Equal(1, rig.Consumer("a").OpenLeases);

        rig.Clock.Advance(TimeSpan.FromHours(0.2));

        Assert.Equal(0, rig.Consumer("a").OpenLeases);
        Assert.Equal(0, rig.Consumer("a").Reserved);
        var expired = Assert.Single(rig.Ledger.DrainEvents(), e => e.Kind == "lease_expired");
        Assert.Equal("a", expired.Consumer);
        Assert.Contains(lease!, expired.Detail, StringComparison.Ordinal);
        Assert.False(rig.Use("a", 1, lease).LeaseClosed);
    }

    [Fact]
    public void Without_an_estimate_the_lease_reserves_the_consumers_median_recent_job()
    {
        var rig = Calibrated();
        rig.Use("a", 2);
        rig.Use("a", 6);

        rig.Ledger.Admit(new AdmitRequest("a"));

        // jobs: 10, 2, 6 → median 6 dollars × 0.005 per dollar
        Assert.Equal(0.03, rig.Consumer("a").Reserved, 9);
    }

    [Fact]
    public void A_lease_ids_are_unique()
    {
        var rig = new Rig();
        var ids = Enumerable.Range(0, 20).Select(_ => rig.Ledger.Admit(new AdmitRequest("a")).LeaseId).ToHashSet();

        Assert.Equal(20, ids.Count);
    }

    [Fact]
    public void A_held_job_opens_no_lease_in_enforce_mode()
    {
        var rig = new Rig(PaceMode.Enforce);
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.UseAndRise("a", 10, 0.08);

        var admission = rig.Ledger.Admit(new AdmitRequest("a"));

        Assert.Equal(Decision.Hold, admission.Decision);
        Assert.Null(admission.LeaseId);
        Assert.Equal(0, rig.Consumer("a").OpenLeases);
    }
}
