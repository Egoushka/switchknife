using Switchknife.Facts.Bench;

namespace Switchknife.Facts.Bench.Tests;

public sealed class HarnessTests
{
    [Fact]
    public void Percentile_is_the_nearest_rank()
    {
        double[] samples = [5, 1, 4, 2, 3, 10, 9, 8, 7, 6];

        Assert.Equal(5, Stats.Percentile(samples, 50));
        Assert.Equal(10, Stats.Percentile(samples, 95));
        Assert.Equal(1, Stats.Percentile(samples, 0));
        Assert.True(double.IsNaN(Stats.Percentile([], 50)));
    }

    [Fact]
    public void Score_counts_matches_by_subject_predicate_and_object_ignoring_case()
    {
        var gold = new[]
        {
            new LabelledEpisode("e1", [new("Yehor", "lives_in", "Kyiv", "2026-01-01"), new("Yehor", "works_at", "Acme")]),
            new LabelledEpisode("e2", [new("Anya", "likes", "tea")]),
        };
        var predicted = new[]
        {
            new LabelledEpisode("e1", [new("yehor", "lives_in", "kyiv ", "2026-02-01"), new("Yehor", "owns", "bike")]),
            new LabelledEpisode("e3", [new("Ghost", "is", "extra")]),
        };

        var score = Scoring.Score(gold, predicted);

        Assert.Equal((1, 2, 2), (score.TruePositives, score.FalsePositives, score.FalseNegatives));
        Assert.Equal(1.0 / 3, score.Precision, 6);
        Assert.Equal(1.0 / 3, score.Recall, 6);
        Assert.Equal(0, score.Dated);
    }

    [Fact]
    public void Score_is_zero_not_NaN_for_empty_input()
    {
        var score = Scoring.Score([], []);

        Assert.Equal(0, score.F1);
    }

    [Fact]
    public void The_same_seed_makes_the_same_history_and_the_oracle_knows_nothing_before_it_was_recorded()
    {
        var first = new Workload(20, 300, seed: 7);
        var second = new Workload(20, 300, seed: 7);

        Assert.Equal(first.Singles, second.Singles);
        var learned = first.Singles[0];
        Assert.Null(first.Expected(learned.Subject, learned.Predicate, learned.ValidFrom, learned.RecordedAt.AddSeconds(-1)));
        Assert.Equal(learned.Value, first.Expected(learned.Subject, learned.Predicate, learned.ValidFrom.AddDays(1), learned.RecordedAt.AddDays(400)));
    }
}
