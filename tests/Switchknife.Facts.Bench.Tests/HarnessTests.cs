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

    [Fact]
    public void Gates_report_every_threshold_a_run_exceeds()
    {
        var fine = new RunReport(
            new RunOptions(1, 1, 1, 1, 1, 1), 1, 1, 1, new Latency(1, 1, 5, 6), new Latency(1, 1, 2, 3), 0, 0, new Latency(1, 1, 2, 3), new Latency(1, 1, 20, 30), 0.97, 0.99,
            1, 0, 100 * 1024 * 1024, 60 * 1024 * 1024, "128MB", 10);

        var slow = fine with
        {
            IngestPerEpisode = new Latency(1, 1, 31, 40),
            AsOf = new Latency(1, 1, 10.5, 12),
            Nearest = new Latency(1, 1, 51, 60),
            FactsBytes = 151L * 1024 * 1024,
            EmbeddingBytes = 101L * 1024 * 1024,
            NearestTieRecall = 0.94,
        };

        Assert.Empty(Gates.Adr.Check(fine));
        Assert.Equal(["ingestPerEpisode.p95", "asOf.p95", "factsBytes", "nearest.p95", "embeddingBytes", "nearestTieRecall"], Gates.Adr.Check(slow).Select(f => f.Split(' ')[0]));
        Assert.Empty(Gates.Adr.Check(fine with { Nearest = null, NearestTieRecall = null, EmbeddingBytes = 0 }));
        Assert.Single(Gates.Adr.Check(fine with { AsOf = new Latency(0, double.NaN, double.NaN, double.NaN) }));
    }
}
