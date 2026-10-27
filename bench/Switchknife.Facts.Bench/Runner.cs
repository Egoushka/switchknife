using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Switchknife.Facts.Bench;

internal sealed record RunOptions(int Subjects, int Facts, int Queries, int EpisodeSize, int VectorDimensions, int Seed, double VectorNoise = 1.0, int Oversample = 40);

/// <summary>Everything one run measured. AsOfWrong and AsOfOverlap must be 0; the rest are the numbers docs/benchmarks/facts-library.md asks for.</summary>
internal sealed record RunReport(
    RunOptions Options,
    int Episodes,
    double IngestSeconds,
    double FactsPerSecond,
    Latency IngestPerEpisode,
    Latency AsOf,
    int AsOfWrong,
    int AsOfOverlap,
    Latency Search,
    Latency? Nearest,
    double? NearestRecall,
    double? NearestTieRecall,
    long LiveRows,
    long RetiredRows,
    long FactsBytes,
    long EmbeddingBytes,
    string SharedBuffers,
    long HarnessPeakRssMb);

internal static class Runner
{
    public const string Namespace = Probes.Namespace;

    private const string Model = "bench-model";
    private const double TieEpsilon = 0.01;

    public static async Task<RunReport> RunAsync(NpgsqlDataSource data, RunOptions options, bool fresh)
    {
        var clock = new ReplayClock();
        var store = new FactsStore(data, Namespace, clock);
        var probes = new Probes(data);
        var workload = new Workload(options.Subjects, options.Facts, options.Seed, options.EpisodeSize);
        await probes.CreateSchemaAsync(fresh).ConfigureAwait(false);
        foreach (var predicate in Workload.SinglePredicates)
        {
            await store.DefinePredicateAsync(predicate, Cardinality.One).ConfigureAwait(false);
        }

        await store.DefinePredicateAsync(Workload.ManyPredicate, Cardinality.Many).ConfigureAwait(false);
        var entities = new Dictionary<Guid, Guid>();
        for (var i = 0; i < workload.Subjects.Count; i++)
        {
            entities[workload.Subjects[i]] = await store.EnsureEntityAsync("person", string.Create(CultureInfo.InvariantCulture, $"person {i}"), string.Create(CultureInfo.InvariantCulture, $"bench:person/{i}")).ConfigureAwait(false);
        }

        var perEpisode = new List<double>();
        var total = Stopwatch.StartNew();
        var number = 0;
        foreach (var episode in workload.Episodes)
        {
            clock.Set(episode.RecordedAt);
            var facts = episode.Singles.Select(s => new NewFact(entities[s.Subject], s.Predicate, s.Value, new DateTimeOffset(s.ValidFrom)))
                .Concat(episode.Many.Select(m => new NewFact(entities[m.Subject], Workload.ManyPredicate, m.Value, new DateTimeOffset(m.ValidFrom))))
                .ToArray();
            var reference = string.Create(CultureInfo.InvariantCulture, $"episode-{number++}");
            var timer = Stopwatch.StartNew();
            await store.IngestAsync(new NewEpisode("bench", reference, reference), facts).ConfigureAwait(false);
            perEpisode.Add(timer.Elapsed.TotalMilliseconds);
        }

        var ingest = total.Elapsed;

        var random = new Random(options.Seed + 1);
        var asOf = new List<double>();
        int wrong = 0, overlap = 0;
        var last = workload.Episodes[^1].RecordedAt;
        for (var i = 0; i < options.Queries; i++)
        {
            var subject = workload.Subjects[random.Next(workload.Subjects.Count)];
            var predicate = Workload.SinglePredicates[random.Next(Workload.SinglePredicates.Length)];
            var validAt = Workload.Origin.AddDays(random.Next(750));
            var knownAt = Workload.Origin.AddDays(random.Next(750) + 40).AddHours(random.Next(24));
            if (knownAt > last)
            {
                knownAt = last;
            }

            var timer = Stopwatch.StartNew();
            var values = await store.AsOfAsync(entities[subject], predicate, new DateTimeOffset(validAt), new DateTimeOffset(knownAt)).ConfigureAwait(false);
            asOf.Add(timer.Elapsed.TotalMilliseconds);
            overlap += values.Count > 1 ? 1 : 0;
            wrong += (values.Count > 0 ? values[0].Value : null) == workload.Expected(subject, predicate, validAt, knownAt) ? 0 : 1;
        }

        var search = new List<double>();
        foreach (var word in await probes.SampleWordsAsync(Math.Min(options.Queries, 500)).ConfigureAwait(false))
        {
            var timer = Stopwatch.StartNew();
            await probes.SearchAsync(word).ConfigureAwait(false);
            search.Add(timer.Elapsed.TotalMilliseconds);
        }

        Latency? nearest = null;
        double? recall = null, tieRecall = null;
        if (options.VectorDimensions > 0)
        {
            (nearest, recall, tieRecall) = await MeasureNearestAsync(store, probes, options).ConfigureAwait(false);
        }

        var sizes = await probes.SizesAsync().ConfigureAwait(false);
        var totalFacts = workload.Singles.Count + workload.Many.Count;
        return new RunReport(options, perEpisode.Count, ingest.TotalSeconds, totalFacts / ingest.TotalSeconds, Stats.Summarize(perEpisode),
            Stats.Summarize(asOf), wrong, overlap, Stats.Summarize(search), nearest, recall, tieRecall, sizes.LiveRows, sizes.RetiredRows, sizes.FactsBytes,
            sizes.EmbeddingBytes, sizes.SharedBuffers, Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024));
    }

    /// <summary>
    /// Embeds every live fact with clustered vectors, then asks for the 10 nearest current facts to fresh draws from the same distribution:
    /// latency of <see cref="FactsStore.NearestAsync"/>, and recall against exact cosine over the original float vectors: strict (the same
    /// ten facts) and tie-aware (the share of results whose exact cosine is within <see cref="TieEpsilon"/> of the exact tenth best). Synthetic
    /// neighbours are nearly tied, so strict recall punishes swapping two facts whose cosines differ by a hair.
    /// </summary>
    private static async Task<(Latency, double, double)> MeasureNearestAsync(FactsStore store, Probes probes, RunOptions options)
    {
        const int K = 10;
        var generator = new VectorWorkload(options.VectorDimensions, 64, options.VectorNoise, options.Seed + 2);
        var live = await probes.LiveFactsAsync().ConfigureAwait(false);
        var vectors = new float[live.Count][];
        foreach (var chunk in Enumerable.Range(0, live.Count).Chunk(2000))
        {
            var batch = new List<FactEmbedding>(chunk.Length);
            foreach (var i in chunk)
            {
                vectors[i] = generator.Next();
                batch.Add(new FactEmbedding(live[i].Id, vectors[i]));
            }

            await store.SetEmbeddingsAsync(Model, batch).ConfigureAwait(false);
        }

        var current = Enumerable.Range(0, live.Count).Where(i => live[i].Current).Select(i => (live[i].Id, Unit: Unit(vectors[i]))).ToArray();
        var validAt = new DateTimeOffset(Workload.Origin.AddDays(5000));
        var timings = new List<double>();
        double recall = 0, tieRecall = 0;
        var queries = Math.Min(options.Queries, 200);
        for (var q = 0; q < queries; q++)
        {
            var query = generator.Next();
            var timer = Stopwatch.StartNew();
            var found = await store.NearestAsync(Model, query, K, validAt, oversample: options.Oversample).ConfigureAwait(false);
            timings.Add(timer.Elapsed.TotalMilliseconds);

            var unit = Unit(query);
            var scored = current.Select(c => (c.Id, Cosine: Dot(unit, c.Unit))).OrderByDescending(c => c.Cosine).ToArray();
            var exact = scored.Take(K).Select(c => c.Id).ToHashSet();
            var cosines = scored.ToDictionary(c => c.Id, c => c.Cosine);
            var floor = scored[K - 1].Cosine - TieEpsilon;
            recall += (double)found.Count(f => exact.Contains(f.Fact.Id)) / K;
            tieRecall += (double)found.Count(f => cosines[f.Fact.Id] >= floor) / K;
        }

        return (Stats.Summarize(timings), recall / queries, tieRecall / queries);
    }

    private static float[] Unit(float[] vector)
    {
        var norm = (float)Math.Sqrt(vector.Sum(x => (double)x * x));
        return [.. vector.Select(x => x / norm)];
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
