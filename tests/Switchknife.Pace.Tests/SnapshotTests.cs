namespace Switchknife.Pace.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void A_ledger_restored_from_a_snapshot_has_the_same_counters_leases_and_meter()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(2);
        rig.UseAndRise("a", 10, 0.05);
        rig.Ledger.Admit(new AdmitRequest("a", 3));
        rig.Meter(0.5, MeterWindow.FiveHour, rejected: true);

        var restored = new Ledger(rig.Policy, rig.Clock, rig.Ledger.Snapshot());

        Assert.Equivalent(rig.Ledger.Status(), restored.Status());
        Assert.Equal(1, restored.Status().Consumers["a"].OpenLeases);
        Assert.Equal(PaceState.Limited, restored.Status().Account.State);
    }

    [Fact]
    public void A_snapshot_is_a_copy()
    {
        var rig = new Rig();
        rig.Meter(0.1);
        var snapshot = rig.Ledger.Snapshot();

        rig.Use("a", 5);
        snapshot.Week!.Weighted["a"] = 99;

        Assert.Equal(5, rig.Consumer("a").WeightedUsd);
    }
}
