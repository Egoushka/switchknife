using Switchknife.Facts;
using Switchknife.Knowledge.Ideas;

namespace Switchknife.Knowledge.Tests;

/// <summary>Spec <c>docs/specs/2026-11-01-knowledge-k3-ideas.md</c>, checks I8-I11.</summary>
public sealed class IdeaStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly IdeaDraft Complete = new(
        IdeaStatus.Testing,
        ValueKind.Money,
        new ValueHypothesis("freelancers", "save 2 h a week", "3 interviews", ExpectedUahPerMonth: 5000m),
        new CheapestTest("landing page", CapHours: 6m, CapUah: 0m, "signups", "10 in 14 days"));

    private static NewEpisode Episode(string source, string hash) => new("note", source, hash);

    private static async Task<string?> StatusAsync(Harness db, Guid idea, int day) =>
        (await db.Facts.AsOfAsync(idea, IdeaPredicates.Status, Harness.Day(day))).SingleOrDefault()?.Value;

    [Fact]
    public async Task I8_capture_writes_title_and_seed_status()
    {
        await using var db = await postgres.NewAsync();
        db.At(1);

        var idea = await db.Ideas.CaptureAsync("Tool that grades ideas", Episode("n1", "h1"));

        var facts = await db.Facts.AsOfAsync(idea, null, Harness.Day(1));
        Assert.Equal(
            [(IdeaPredicates.Status, "seed"), (IdeaPredicates.Title, "Tool that grades ideas")],
            facts.Select(f => (f.Predicate, f.Value)));
    }

    [Fact]
    public async Task I9_incomplete_promotion_writes_nothing_and_returns_codes()
    {
        await using var db = await postgres.NewAsync();
        db.At(1);
        var idea = await db.Ideas.CaptureAsync("x", Episode("n1", "h1"));
        db.At(2);

        var rejected = await db.Ideas.MoveAsync(idea, Complete with { Test = null }, Episode("n2", "h2"));

        Assert.False(rejected.Accepted);
        Assert.Equal([new IdeaRejection(IdeaRejectReason.Missing, "cheapest_test")], rejected.Rejections);
        Assert.Equal("seed", await StatusAsync(db, idea, 2));
        Assert.Empty(await db.Facts.AsOfAsync(idea, IdeaPredicates.ValueKind, Harness.Day(2)));

        var accepted = await db.Ideas.MoveAsync(idea, Complete, Episode("n2", "h2"));

        Assert.True(accepted.Accepted);
        var facts = await db.Facts.AsOfAsync(idea, null, Harness.Day(2));
        Assert.Equal(
            [IdeaPredicates.CheapestTest, IdeaPredicates.Status, IdeaPredicates.Title, IdeaPredicates.ValueHypothesis, IdeaPredicates.ValueKind],
            facts.Select(f => f.Predicate));
        Assert.Equal(4, accepted.Ingest!.Written);
    }

    [Fact]
    public async Task I10_one_live_status_and_earlier_ones_superseded()
    {
        await using var db = await postgres.NewAsync();
        db.At(1);
        var idea = await db.Ideas.CaptureAsync("x", Episode("n1", "h1"));
        db.At(2);
        await db.Ideas.MoveAsync(idea, Complete, Episode("n2", "h2"));
        db.At(3);
        var validated = await db.Ideas.MoveAsync(idea, Complete with { Status = IdeaStatus.Validated }, Episode("n3", "h3"));

        Assert.True(validated.Accepted);
        Assert.Equal("validated", await StatusAsync(db, idea, 3));
        var history = await db.Facts.HistoryAsync(idea, IdeaPredicates.Status);
        Assert.Single(history, v => v.RetiredAt is null && v.Fact.ValidTo is null);
        Assert.Equal(["seed", "testing"], history.Where(v => v.RetiredReason == "superseded").Select(v => v.Fact.Value).Distinct().Order());
    }

    [Fact]
    public async Task I11_revive_goes_through_the_gate_and_retires_the_kill_reason()
    {
        await using var db = await postgres.NewAsync();
        db.At(1);
        var idea = await db.Ideas.CaptureAsync("x", Episode("n1", "h1"));
        db.At(2);
        await db.Ideas.MoveAsync(idea, new IdeaDraft(IdeaStatus.Killed, KillReason: "no audience"), Episode("n2", "h2"));
        Assert.Equal("no audience", Assert.Single(await db.Facts.AsOfAsync(idea, IdeaPredicates.KillReason, Harness.Day(2))).Value);
        db.At(3);

        var gated = await db.Ideas.MoveAsync(idea, new IdeaDraft(IdeaStatus.Testing), Episode("n3", "h3"));
        Assert.False(gated.Accepted);
        Assert.Equal("killed", await StatusAsync(db, idea, 3));

        var revived = await db.Ideas.MoveAsync(idea, Complete, Episode("n4", "h4"));

        Assert.True(revived.Accepted);
        Assert.Equal("testing", await StatusAsync(db, idea, 3));
        Assert.Empty(await db.Facts.AsOfAsync(idea, IdeaPredicates.KillReason, Harness.Day(3)));
        var history = Assert.Single(await db.Facts.HistoryAsync(idea, IdeaPredicates.KillReason));
        Assert.Equal(("no audience", "retracted"), (history.Fact.Value, history.RetiredReason));
    }
}
