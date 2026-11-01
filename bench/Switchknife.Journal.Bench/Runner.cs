using System.Diagnostics;
using System.Globalization;

namespace Switchknife.Journal.Bench;

internal sealed record Size(string Label, int Bytes, int Runs);

internal sealed record Row(string Store, string Scenario, string Size, Latency Latency);

internal static class Runner
{
    public static readonly Size[] DefaultSizes = [new("1 KB", 1024, 5000), new("10 KB", 10 * 1024, 3000), new("100 KB", 100 * 1024, 1000)];

    /// <summary>
    /// <paramref name="writers"/> tasks append <paramref name="total"/> events together, each to its own room or all to one;
    /// each call is timed from before the call to after it returns. A warm-up of 10% (at least 50) goes first and is dropped.
    /// </summary>
    public static async Task<Row> RunAsync(IAppender appender, string scenario, Size size, Bodies bodies, int writers, bool sharedRoom, int total)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        Console.Error.WriteLine($"  {appender.Name} / {scenario} / {size.Label}");
        var warmup = Math.Max(50, total / 10);
        await Drive(appender, tag + "w", bodies, writers, sharedRoom, warmup, record: false).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var samples = await Drive(appender, tag + "m", bodies, writers, sharedRoom, total, record: true).ConfigureAwait(false);
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        return new Row(appender.Name, scenario, size.Label, Stats.Summarize(samples, seconds));
    }

    private static async Task<List<double>> Drive(IAppender appender, string tag, Bodies bodies, int writers, bool sharedRoom, int total, bool record)
    {
        var samples = new List<double>(total);
        var perWriter = total / writers;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async w =>
        {
            var room = $"bench/{tag}/{(sharedRoom ? 0 : w)}";
            var mine = new List<double>(perWriter);
            for (var i = 0; i < perWriter; i++)
            {
                var body = bodies[(w * perWriter) + i];
                var start = Stopwatch.GetTimestamp();
                await appender.AppendAsync(room, $"{tag}-{w}-{i}", body, CancellationToken.None).ConfigureAwait(false);
                mine.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            if (record)
            {
                lock (samples)
                {
                    samples.AddRange(mine);
                }
            }
        })).ConfigureAwait(false);
        return samples;
    }

    /// <summary>CPU cost of the three hashes over one body, in microseconds (p50, p95, p99), plus SHA-256 throughput of the body pass.</summary>
    public static string HashRow(Size size, Bodies bodies)
    {
        var iterations = Math.Max(2000, 40_000_000 / size.Bytes);
        var prev = Chain.Genesis;
        var bodyHash = Chain.BodyHash(bodies[0]);
        var body = Time(iterations, i => Chain.BodyHash(bodies[i]));
        var link = Time(iterations, i => Chain.Link("bench/room/0", i, DateTimeOffset.UtcNow, "claude", "tool_result", [], "none", "source-" + i, bodyHash, prev));
        var naive = Time(iterations, i => Chain.BodyThenPrev(bodies[i], prev));
        var mbPerSecond = size.Bytes * iterations / 1e6 / (body.Mean * iterations / 1e6);
        return string.Create(CultureInfo.InvariantCulture,
            $"| {size.Label} | {iterations} | {Cell(body)} | {Cell(link)} | {Cell(naive)} | {mbPerSecond:F0} |");
    }

    private static string Cell((double P50, double P95, double P99, double Mean) t) =>
        string.Create(CultureInfo.InvariantCulture, $"{t.P50:F1} / {t.P95:F1} / {t.P99:F1}");

    private static (double P50, double P95, double P99, double Mean) Time(int iterations, Func<int, byte[]> work)
    {
        for (var i = 0; i < Math.Min(iterations, 200); i++)
        {
            work(i);
        }

        var micros = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            work(i);
            micros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }

        return (Stats.Percentile(micros, 50), Stats.Percentile(micros, 95), Stats.Percentile(micros, 99), micros.Average());
    }
}
