namespace Switchknife.Pace.Tests;

public sealed class PolicyTests
{
    private static Policy With(params (string Name, ConsumerPolicy Consumer)[] consumers) =>
        new() { Consumers = consumers.ToDictionary(c => c.Name, c => c.Consumer) };

    [Fact]
    public void A_sound_policy_has_no_problems()
    {
        Assert.Empty(With(("a", new(0.5)), ("owner", new(0.5))).Validate());
    }

    [Fact]
    public void Shares_summing_past_one_are_a_problem()
    {
        var problem = Assert.Single(With(("a", new(0.6)), ("b", new(0.5))).Validate());

        Assert.Contains("shares sum to 1.1", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_share_outside_zero_to_one_is_a_problem()
    {
        Assert.Contains(With(("a", new(1.5))).Validate(), p => p.Contains("'a'", StringComparison.Ordinal));
        Assert.Contains(With(("a", new(-0.1))).Validate(), p => p.Contains("'a'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_action_must_be_hold_api_or_cheap()
    {
        Assert.Contains(With(("a", new(0.1, Decision.Allow))).Validate(), p => p.Contains("action must be", StringComparison.Ordinal));
        Assert.Contains(With(("a", new(0.1, OnStale: (Decision)9))).Validate(), p => p.Contains("on_stale must be", StringComparison.Ordinal));
    }

    [Fact]
    public void The_owner_may_not_have_an_action()
    {
        Assert.Contains(With(("owner", new(0.5, Decision.Hold))).Validate(), p => p.Contains("never gated", StringComparison.Ordinal));
    }

    [Fact]
    public void A_ledger_will_not_start_on_an_invalid_policy_and_says_why()
    {
        var error = Assert.Throws<ArgumentException>(() => new Ledger(With(("a", new(2)))));

        Assert.Contains("share 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Durations_and_weights_must_make_sense()
    {
        var policy = With(("a", new(0.1))) with
        {
            StaleAfter = TimeSpan.Zero,
            LeaseTtl = TimeSpan.FromSeconds(-1),
            ModelWeights = new Dictionary<string, double> { ["m"] = -1 },
        };

        Assert.Equal(3, policy.Validate().Count);
    }
}
