using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Switchknife.Facts.Bench;

internal sealed record RunOptions(int Subjects, int Facts, int Queries, int EpisodeSize, int VectorDimensions, int Seed);

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
    long LiveRows,
    long RetiredRows,
    long FactsBytes,
    long EmbeddingBytes,
    string SharedBuffers,
    long HarnessPeakRssMb);

internal static class Runner
{
    public const string Namespace = Probes.Namespace;

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
        if (options.VectorDimensions > 0)
        {
            await probes.EmbedLiveAsync(options.VectorDimensions, options.Seed + 2).ConfigureAwait(false);
            var timings = new List<double>();
            for (var i = 0; i < Math.Min(options.Queries, 200); i++)
            {
                var vector = Probes.RandomVector(random, options.VectorDimensions);
                var timer = Stopwatch.StartNew();
                await probes.NearestAsync(vector).ConfigureAwait(false);
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }

            nearest = Stats.Summarize(timings);
        }

        var sizes = await probes.SizesAsync().ConfigureAwait(false);
        var totalFacts = workload.Singles.Count + workload.Many.Count;
        return new RunReport(options, perEpisode.Count, ingest.TotalSeconds, totalFacts / ingest.TotalSeconds, Stats.Summarize(perEpisode),
            Stats.Summarize(asOf), wrong, overlap, Stats.Summarize(search), nearest, sizes.LiveRows, sizes.RetiredRows, sizes.FactsBytes,
            sizes.EmbeddingBytes, sizes.SharedBuffers, Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024));
    }
}
