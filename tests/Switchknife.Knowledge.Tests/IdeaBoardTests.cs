using System.Text.Json;
using Switchknife.Facts;
using Switchknife.Knowledge.Ideas;

namespace Switchknife.Knowledge.Tests;

/// <summary>Spec <c>docs/specs/2026-11-01-knowledge-k3-ideas.md</c>, checks I16-I19.</summary>
public sealed class IdeaBoardTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly IdeaDraft Testing = new(
        IdeaStatus.Testing,
        ValueKind.Money,
        new ValueHypothesis("freelancers", "save 2 h a week", "3 interviews", ExpectedUahPerMonth: 5000m),
        new CheapestTest("landing page", CapHours: 6m, CapUah: 0m, "signups", "10 in 14 days"));

    private int _episodes;

    private NewEpisode Next() => new("note", $"n{++_episodes}", $"h{_episodes}");

    private async Task<Guid> SeedAsync(Harness db, int day, string title)
    {
        db.At(day);
        return await db.Ideas.CaptureAsync(title, Next());
    }

    private async Task MoveAsync(Harness db, Guid idea, int day, IdeaDraft draft)
    {
        db.At(day);
        Assert.True((await db.Ideas.MoveAsync(idea, draft, Next())).Accepted);
    }

    [Fact]
    public async Task I16_chain_returns_links_days_and_earned()
    {
        await using var db = await postgres.NewAsync();
        var source = await SeedAsync(db, 1, "Notes on grading");
        var idea = await SeedAsync(db, 1, "Tool that grades ideas");
        var bare = await SeedAsync(db, 1, "Unlinked idea");
        db.At(2);
        await db.Ideas.RecordInspirationAsync(source, idea, Next());
        await MoveAsync(db, idea, 3, Testing);
        db.At(5);
        var experiment = await db.Ideas.StartExperimentAsync(idea, Next());
        db.At(12);
        var result = await db.Ideas.RecordResultAsync(experiment, ResultOutcome.Passed, Next(), metricValue: 14m, note: "14 signups");
        db.At(20);
        await db.Ideas.RecordEarnedAsync(result, 3000m, Harness.Day(20), Next());
        db.At(30);

        var chain = await db.Ideas.ChainAsync(idea);

        Assert.Equal("testing", chain.Status);
        Assert.Equal(
            [
                new ChainStage("inspired", Harness.Day(2), 3),
                new ChainStage("tested_by", Harness.Day(5), 7),
                new ChainStage("produced", Harness.Day(12), 8),
                new ChainStage("earned", Harness.Day(20), 10),
            ],
            chain.Stages);
        Assert.Equal(3000m, chain.EarnedUah);
        Assert.Equal(
            [("metric_value", "14"), ("note", "14 signups"), ("outcome", "passed")],
            (await db.Facts.AsOfAsync(result, null, Harness.Day(30))).Select(f => (f.Predicate, f.Value)).OrderBy(p => p.Predicate, StringComparer.Ordinal));

        var none = await db.Ideas.ChainAsync(bare);
        Assert.Equal(("seed", 0, 0m), (none.Status, none.Stages.Count, none.EarnedUah));
    }

    [Fact]
    public async Task I17_estimated_and_earned_stay_apart()
    {
        await using var db = await postgres.NewAsync();
        var a = await SeedAsync(db, 1, "a");
        var b = await SeedAsync(db, 1, "b");
        await MoveAsync(db, a, 2, Testing);
        await MoveAsync(db, b, 2, Testing with { Hypothesis = Testing.Hypothesis! with { ExpectedUahPerMonth = 8000m } });
        db.At(3);
        var result = await db.Ideas.RecordResultAsync(await db.Ideas.StartExperimentAsync(a, Next()), ResultOutcome.Passed, Next());
        await db.Ideas.RecordEarnedAsync(result, 3000m, Harness.Day(3), Next());

        var board = await db.Ideas.BoardAsync(targetUahPerMonth: 60000m);

        Assert.Equal(new IdeaMoney(13000m, 3000m, 3000m, 60000m), board.Money);
        var json = JsonSerializer.Serialize(board, IdeaBoardJsonContext.Default.IdeaBoard);
        Assert.DoesNotContain("16000", json, StringComparison.Ordinal);
        Assert.Contains("\"estimated_uah_per_month\":13000", json, StringComparison.Ordinal);
        Assert.Contains("\"earned_uah\":3000", json, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => db.Ideas.RecordEarnedAsync(result, -1m, Harness.Day(3), Next()).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task I18_funnel_median_days_and_stuck_tests_match_the_fixture()
    {
        await using var db = await postgres.NewAsync();
        // one: seed 0, testing 4, validated 10, earning 13. two: seed 1, testing 3 with a 5-day box. three: seed 2, killed 8.
        // four: seed 5. five: seed 0, testing 6 with no box.
        var one = await SeedAsync(db, 0, "one");
        var five = await SeedAsync(db, 0, "five");
        var two = await SeedAsync(db, 1, "two");
        var three = await SeedAsync(db, 2, "three");
        await MoveAsync(db, two, 3, Testing with { Test = Testing.Test! with { TimeBoxDays = 5 } });
        await MoveAsync(db, one, 4, Testing);
        await SeedAsync(db, 5, "four");
        await MoveAsync(db, five, 6, Testing);
        await MoveAsync(db, three, 8, new IdeaDraft(IdeaStatus.Killed, KillReason: "no demand"));
        await MoveAsync(db, one, 10, Testing with { Status = IdeaStatus.Validated });
        await MoveAsync(db, one, 13, Testing with { Status = IdeaStatus.Earning });
        db.At(20);

        var board = await db.Ideas.BoardAsync();

        Assert.Equal(new StatusCounts(Seed: 1, Testing: 2, Validated: 0, Killed: 1, Earning: 1), board.Statuses);
        Assert.Equal(new IdeaFunnel(5, 3, 1, 1, 3 / 5d, 1 / 3d, 1d), board.Funnel);
        // Seed stays that ended: 4, 2, 6, 6 → 5. Testing: 6. Validated: 3.
        Assert.Equal(new StageDays(5, 6, 3), board.MedianDays);
        Assert.Equal([new StuckTest(two, "two", Harness.Day(3), 5, 17)], board.StuckTests);
        Assert.Equal([five], board.UnboxedTests);
        Assert.Equal(new IdeaMoney(10000m, 0m, 0m, null), board.Money);
    }

    [Fact]
    public async Task I19_absent_measures_are_null_with_a_reason()
    {
        await using var db = await postgres.NewAsync();

        var board = await db.Ideas.BoardAsync();

        Assert.Null(board.LateYield);
        Assert.Null(board.QuestionCoverage);
        Assert.Equal(["late_yield", "question_coverage"], board.Absent.Select(a => a.Name));
        Assert.All(board.Absent, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
        Assert.Null(board.Funnel.SeedToTesting);
        var json = JsonSerializer.Serialize(board, IdeaBoardJsonContext.Default.IdeaBoard);
        Assert.Contains("\"late_yield\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"question_coverage\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Time_box_must_be_positive()
    {
        var draft = Testing with { Test = Testing.Test! with { TimeBoxDays = 0 } };

        Assert.Equal([new IdeaRejection(IdeaRejectReason.NotPositive, "cheapest_test.time_box_days")], IdeaValidator.Validate(IdeaStatus.Seed, draft));
        Assert.Equal(7, IdeaJson.ReadTest(IdeaJson.Write(Testing.Test! with { TimeBoxDays = 7 }))!.TimeBoxDays);
    }
}
