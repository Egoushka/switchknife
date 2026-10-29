namespace Switchknife.Pace.Tests;

public sealed class ObserveTests
{
    [Fact]
    public void ObserveNeverDenies_a_consumer_at_three_times_its_share_gets_allow_with_would_hold()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.AdvanceDays(3.5);
        rig.UseAndRise("a", 10, 0.375);

        var admission = rig.Ledger.Admit(new AdmitRequest("a"));

        Assert.Equal(0.375, rig.Consumer("a").Used, 9);
        Assert.Equal(0.25 * 0.5 * 3, rig.Consumer("a").Used, 9);
        Assert.Equal(Decision.Allow, admission.Decision);
        Assert.Equal(Decision.Hold, admission.Would);
        Assert.NotNull(admission.LeaseId);
    }

    [Fact]
    public void Enforce_mode_answers_what_observe_mode_only_says()
    {
        var rig = new Rig(PaceMode.Enforce);
        rig.Meter(0.0);
        rig.AdvanceDays(3.5);
        rig.UseAndRise("a", 10, 0.375);

        var admission = rig.Ledger.Admit(new AdmitRequest("a"));

        Assert.Equal(Decision.Hold, admission.Decision);
        Assert.Equal(Decision.Hold, admission.Would);
    }

    [Theory]
    [InlineData(PaceMode.Observe)]
    [InlineData(PaceMode.Enforce)]
    public void The_owner_is_measured_and_never_gated(PaceMode mode)
    {
        var rig = new Rig(mode);
        rig.Meter(0.0);
        rig.AdvanceDays(1);
        rig.UseAndRise("owner", 10, 0.90);
        rig.Meter(0.9, MeterWindow.FiveHour, rejected: true);

        var admission = rig.Ledger.Admit(new AdmitRequest("owner"));

        Assert.Equal(Decision.Allow, admission.Decision);
        Assert.Equal(Decision.Allow, admission.Would);
        Assert.Null(admission.LeaseId);
        Assert.Equal(0.90, rig.Consumer("owner").Used, 9);
    }

    [Fact]
    public void The_owner_need_not_be_listed()
    {
        var policy = new Policy { Consumers = new Dictionary<string, ConsumerPolicy> { ["a"] = new(0.5) } };
        var ledger = new Ledger(policy, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Rig.Start));

        ledger.Report(new UsageReport("owner", new Dictionary<string, double> { ["m"] = 1 }));

        Assert.Equal(1, ledger.Status().Consumers["owner"].WeightedUsd);
    }

    [Fact]
    public void An_unknown_consumer_is_refused_by_name()
    {
        var rig = new Rig();

        Assert.Equal("ghost", Assert.Throws<UnknownConsumerException>(() => rig.Ledger.Admit(new AdmitRequest("ghost"))).Consumer);
        Assert.Throws<UnknownConsumerException>(() => rig.Use("ghost", 1));
    }

    [Fact]
    public void Costs_and_estimates_must_not_be_negative()
    {
        var rig = new Rig();

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Use("a", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Ledger.Admit(new AdmitRequest("a", -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Meter(double.NaN));
    }

    [Fact]
    public void Model_weights_scale_the_reported_cost()
    {
        var policy = new Policy
        {
            Consumers = new Dictionary<string, ConsumerPolicy> { ["a"] = new(0.5) },
            ModelWeights = new Dictionary<string, double> { ["small"] = 0.2 },
        };
        var ledger = new Ledger(policy, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Rig.Start));

        var result = ledger.Report(new UsageReport("a", new Dictionary<string, double> { ["small"] = 10, ["big"] = 3 }));

        Assert.Equal(5, result.WeightedUsd, 9);
    }

    [Fact]
    public void API_use_counts_toward_the_api_total_and_not_the_share()
    {
        var rig = new Rig();
        rig.Meter(0.0);
        rig.Ledger.Report(new UsageReport("a", new Dictionary<string, double> { ["m"] = 3 }, Route: UsageRoute.Api));

        var a = rig.Consumer("a");
        Assert.Equal(3, a.ApiUsd);
        Assert.Equal(0, a.WeightedUsd);
        rig.Meter(0.05);
        Assert.Equal(0, rig.Consumer("a").Used);
    }
}
