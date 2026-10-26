using System.Diagnostics;
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
    public static async Task<RunReport> RunAsync(NpgsqlDataSource data, RunOptions options, bool fresh)
    {
        var store = new FactsStore(data);
        var workload = new Workload(options.Subjects, options.Facts, options.Seed);
        await store.CreateSchemaAsync(fresh).ConfigureAwait(false);
        await store.SeedAsync(workload, Workload.Origin).ConfigureAwait(false);

        // Learned order is recorded order; singles and many-valued facts are merged into episodes by time.
        var timeline = workload.Singles.Select(s => (s.RecordedAt, Single: (Learned?)s, Many: (Added?)null))
            .Concat(workload.Many.Select(m => (m.RecordedAt, Single: (Learned?)null, Many: (Added?)m)))
            .OrderBy(e => e.RecordedAt).ToList();

        var perEpisode = new List<double>();
        var total = Stopwatch.StartNew();
        var episode = 0;
        foreach (var chunk in timeline.Chunk(options.EpisodeSize))
        {
            var clock = Stopwatch.StartNew();
            await store.IngestAsync([.. chunk.Where(e => e.Single is not null).Select(e => e.Single!)], [.. chunk.Where(e => e.Many is not null).Select(e => e.Many!)], episode++)
                .ConfigureAwait(false);
            perEpisode.Add(clock.Elapsed.TotalMilliseconds);
        }

        var ingest = total.Elapsed;

        var random = new Random(options.Seed + 1);
        var asOf = new List<double>();
        int wrong = 0, overlap = 0;
        var last = workload.Singles[^1].RecordedAt;
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

            var clock = Stopwatch.StartNew();
            var values = await store.AsOfAsync(subject, predicate, validAt, knownAt).ConfigureAwait(false);
            asOf.Add(clock.Elapsed.TotalMilliseconds);
            overlap += values.Count > 1 ? 1 : 0;
            wrong += (values.Count > 0 ? values[0] : null) == workload.Expected(subject, predicate, validAt, knownAt) ? 0 : 1;
        }

        var search = new List<double>();
        foreach (var word in await store.SampleWordsAsync(Math.Min(options.Queries, 500)).ConfigureAwait(false))
        {
            var clock = Stopwatch.StartNew();
            await store.SearchAsync(word).ConfigureAwait(false);
            search.Add(clock.Elapsed.TotalMilliseconds);
        }

        Latency? nearest = null;
        if (options.VectorDimensions > 0)
        {
            await store.EmbedLiveAsync(options.VectorDimensions, options.Seed + 2).ConfigureAwait(false);
            var timings = new List<double>();
            for (var i = 0; i < Math.Min(options.Queries, 200); i++)
            {
                var vector = FactsStore.RandomVector(random, options.VectorDimensions);
                var clock = Stopwatch.StartNew();
                await store.NearestAsync(vector).ConfigureAwait(false);
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }

            nearest = Stats.Summarize(timings);
        }

        var sizes = await store.SizesAsync().ConfigureAwait(false);
        return new RunReport(options, perEpisode.Count, ingest.TotalSeconds, timeline.Count / ingest.TotalSeconds, Stats.Summarize(perEpisode),
            Stats.Summarize(asOf), wrong, overlap, Stats.Summarize(search), nearest, sizes.LiveRows, sizes.RetiredRows, sizes.FactsBytes,
            sizes.EmbeddingBytes, sizes.SharedBuffers, Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024));
    }
}
