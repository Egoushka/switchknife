namespace Switchknife.Pace.Tests;

public sealed class CumulativeSessionUsageTests
{
    private static UsageReport Total(double usd) =>
        new("owner", new Dictionary<string, double> { ["m"] = usd }, SessionId: "s1", Cumulative: true);

    [Fact]
    public void One_sessions_running_totals_count_the_highest_total_once()
    {
        var rig = new Rig();
        foreach (var total in new[] { 0.10, 0.25, 0.25, 0.40 })
        {
            rig.Ledger.Report(Total(total));
        }

        Assert.Equal(0.40, rig.Consumer("owner").WeightedUsd, 9);
    }

    [Fact]
    public void A_lower_total_is_ignored_and_a_later_higher_one_counts_only_the_increase()
    {
        var rig = new Rig();
        rig.Ledger.Report(Total(0.40));

        Assert.Equal(0, rig.Ledger.Report(Total(0.30)).WeightedUsd);
        Assert.Equal(0.10, rig.Ledger.Report(Total(0.50)).WeightedUsd, 9);
        Assert.Equal(0.50, rig.Consumer("owner").WeightedUsd, 9);
    }

    [Fact]
    public void Sessions_are_counted_separately()
    {
        var rig = new Rig();
        rig.Ledger.Report(Total(0.40));
        rig.Ledger.Report(Total(0.40) with { SessionId = "s2" });

        Assert.Equal(0.80, rig.Consumer("owner").WeightedUsd, 9);
    }

    [Fact]
    public void A_session_total_survives_a_snapshot_so_a_repeat_after_a_restart_adds_nothing()
    {
        var rig = new Rig();
        rig.Ledger.Report(Total(0.40));

        var restored = new Ledger(rig.Policy, rig.Clock, rig.Ledger.Snapshot());

        Assert.Equal(0, restored.Report(Total(0.40)).WeightedUsd);
    }
}
