using Switchknife.Knowledge.Ideas;

namespace Switchknife.Knowledge.Tests;

public class IdeaValidatorTests
{
    private static readonly ValueHypothesis Hypothesis = new("freelancers", "save 2 h a week", "3 interviews", 5000m);
    private static readonly CheapestTest Test = new("landing page", 6m, 0m, "signups", "10 in 14 days");

    private static IdeaDraft Full(IdeaStatus status, ValueKind kind = ValueKind.Money) =>
        new(status, kind, Hypothesis, Test);

    [Fact]
    public void Seed_with_nothing_is_accepted()
    {
        Assert.Empty(IdeaValidator.Validate(new IdeaDraft(IdeaStatus.Seed)));
    }

    [Theory]
    [InlineData(IdeaStatus.Testing)]
    [InlineData(IdeaStatus.Validated)]
    [InlineData(IdeaStatus.Earning)]
    public void Promotion_without_anything_reports_each_gap(IdeaStatus status)
    {
        var found = IdeaValidator.Validate(new IdeaDraft(status));

        Assert.Equal(["value_kind", "value_hypothesis", "cheapest_test"], found.Select(r => r.Field));
        Assert.All(found, r => Assert.Equal(IdeaRejectReason.Missing, r.Reason));
    }

    [Fact]
    public void Complete_promotion_is_accepted()
    {
        Assert.Empty(IdeaValidator.Validate(Full(IdeaStatus.Testing)));
    }

    [Theory]
    [InlineData("", "gain", "evidence", "value_hypothesis.who")]
    [InlineData("who", " ", "evidence", "value_hypothesis.gain")]
    [InlineData("who", "gain", null, "value_hypothesis.evidence")]
    public void Blank_hypothesis_part_is_named(string? who, string? gain, string? evidence, string field)
    {
        var draft = Full(IdeaStatus.Testing) with { Hypothesis = new(who!, gain!, evidence!, 1m) };

        Assert.Equal([new IdeaRejection(IdeaRejectReason.Missing, field)], IdeaValidator.Validate(draft));
    }

    [Fact]
    public void Blank_test_parts_are_named()
    {
        var draft = Full(IdeaStatus.Testing) with { Test = new("", 1m, 0m, " ", "") };

        Assert.Equal(["cheapest_test.action", "cheapest_test.metric", "cheapest_test.pass_line"], IdeaValidator.Validate(draft).Select(r => r.Field));
    }

    [Fact]
    public void Test_needs_a_positive_hours_cap_and_a_non_negative_uah_cap()
    {
        var draft = Full(IdeaStatus.Testing) with { Test = Test with { CapHours = 0m, CapUah = -1m } };

        Assert.Equal(
            [new IdeaRejection(IdeaRejectReason.NotPositive, "cheapest_test.cap_hours"), new IdeaRejection(IdeaRejectReason.Negative, "cheapest_test.cap_uah")],
            IdeaValidator.Validate(draft));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Money_needs_a_positive_estimate(int? estimate)
    {
        var draft = Full(IdeaStatus.Testing) with { Hypothesis = Hypothesis with { ExpectedUahPerMonth = estimate } };

        Assert.Equal([new IdeaRejection(IdeaRejectReason.NotPositive, "value_hypothesis.expected_uah_per_month")], IdeaValidator.Validate(draft));
    }

    [Fact]
    public void People_needs_no_estimate()
    {
        var draft = Full(IdeaStatus.Testing, ValueKind.People) with { Hypothesis = Hypothesis with { ExpectedUahPerMonth = null } };

        Assert.Empty(IdeaValidator.Validate(draft));
    }

    [Fact]
    public void Kill_needs_a_reason_but_not_a_hypothesis()
    {
        Assert.Equal([new IdeaRejection(IdeaRejectReason.Missing, "kill_reason")], IdeaValidator.Validate(new IdeaDraft(IdeaStatus.Killed)));
        Assert.Empty(IdeaValidator.Validate(new IdeaDraft(IdeaStatus.Killed, KillReason: "pass line missed")));
    }

    [Fact]
    public void Transitions_match_the_table_over_every_pair()
    {
        var allowed = new HashSet<(IdeaStatus, IdeaStatus)>
        {
            (IdeaStatus.Seed, IdeaStatus.Testing), (IdeaStatus.Seed, IdeaStatus.Killed),
            (IdeaStatus.Testing, IdeaStatus.Validated), (IdeaStatus.Testing, IdeaStatus.Killed),
            (IdeaStatus.Validated, IdeaStatus.Earning), (IdeaStatus.Validated, IdeaStatus.Killed),
            (IdeaStatus.Earning, IdeaStatus.Killed),
            (IdeaStatus.Killed, IdeaStatus.Seed), (IdeaStatus.Killed, IdeaStatus.Testing),
        };

        foreach (var from in Enum.GetValues<IdeaStatus>())
        {
            foreach (var to in Enum.GetValues<IdeaStatus>())
            {
                Assert.Equal(allowed.Contains((from, to)), IdeaValidator.CanMove(from, to));
            }
        }
    }

    [Fact]
    public void Refused_move_is_reported_before_the_draft_is_checked()
    {
        var found = IdeaValidator.Validate(IdeaStatus.Seed, Full(IdeaStatus.Earning));

        Assert.Equal([new IdeaRejection(IdeaRejectReason.TransitionNotAllowed, "status")], found);
    }

    [Fact]
    public void Revival_goes_through_the_full_gate()
    {
        Assert.NotEmpty(IdeaValidator.Validate(IdeaStatus.Killed, new IdeaDraft(IdeaStatus.Testing)));
        Assert.Empty(IdeaValidator.Validate(IdeaStatus.Killed, Full(IdeaStatus.Testing)));
    }

    [Fact]
    public void Rejection_text_never_carries_draft_content()
    {
        const string secret = "SECRET-PASS-LINE-42";
        var draft = Full(IdeaStatus.Testing) with { Test = new(secret, 0m, 0m, secret, secret), Hypothesis = new(secret, "", secret, 1m) };

        var text = string.Join(';', IdeaValidator.Validate(draft).Select(r => r.ToString()));

        Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void Json_values_round_trip_in_snake_case()
    {
        var json = IdeaJson.Write(Test);

        Assert.Contains("\"cap_hours\":6", json);
        Assert.Equal(Test, IdeaJson.ReadTest(json));
        Assert.Equal(Hypothesis, IdeaJson.ReadHypothesis(IdeaJson.Write(Hypothesis)));
        Assert.Null(IdeaJson.ReadTest("not json"));
    }

    [Fact]
    public void Predicates_are_unique()
    {
        var names = IdeaPredicates.All.Select(p => p.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct().Count());
    }
}
