using System.Globalization;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace Switchknife.Pace.Tests;

/// <summary>
/// A seeded week (subgate ADR 0005): three consumers with shares 25/10/10 who report every job, an owner who uses the other
/// 45 points of the 90 the week ends at, a meter quantised to 0.01 read every 30 minutes and after each report, reports out of
/// order by up to 30 s, 40 seeds per cell. The owner is idle in quiet hours (00:00 to 08:00 UTC). A cell is the share of the
/// owner's jobs that are never reported (none, half, all) and the error in each job's reported weight (0 or ±30%).
/// </summary>
public sealed class AttributionSimulationTests
{
    private const double PointsPerDollar = 0.002;
    private const int Seeds = 40;
    private static readonly string[] Consumers = ["a", "b", "c"];
    private static readonly double[] Targets = [0.25, 0.10, 0.10];

    public static TheoryData<double, double> Cells => new()
    {
        { 0.0, 0.0 }, { 0.0, 0.3 }, { 0.5, 0.0 }, { 0.5, 0.3 }, { 1.0, 0.0 }, { 1.0, 0.3 },
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public void AttributionSimulation_the_worst_consumers_p95_error_is_within_five_points(double ownerUnreported, double weightError)
    {
        var errors = Consumers.ToDictionary(c => c, _ => new List<double>());
        for (var seed = 1; seed <= Seeds; seed++)
        {
            foreach (var (consumer, error) in RunWeek(seed, ownerUnreported, weightError))
            {
                errors[consumer].Add(error);
            }
        }

        var worst = errors.Values.Max(P95);
        Record(ownerUnreported, weightError, worst);
        Assert.True(worst <= 5.0, $"worst p95 error {worst:0.00} points with {ownerUnreported:P0} of the owner unreported and ±{weightError:P0} weights");
    }

    [Fact]
    public void AttributionSimulation_is_deterministic_for_a_seed()
    {
        Assert.Equal(RunWeek(7, 0.5, 0.3), RunWeek(7, 0.5, 0.3));
    }

    private static double P95(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];
    }

    private static void Record(double ownerUnreported, double weightError, double worst)
    {
        if (Environment.GetEnvironmentVariable("SWITCHKNIFE_PACE_SIM_OUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture, $"unreported={ownerUnreported} weight_error={weightError} worst_p95_points={worst:0.00}\n"));
        }
    }

    /// <summary>Returns each consumer's absolute error at the end of the week, in points of the week.</summary>
    private static List<(string Consumer, double Error)> RunWeek(int seed, double ownerUnreported, double weightError)
    {
        var rng = new Random(seed);
        var start = Rig.Start;
        var reset = Rig.Reset;
        var clock = new FakeTimeProvider(start);
        var policy = new Policy
        {
            Consumers = new Dictionary<string, ConsumerPolicy> { ["a"] = new(0.25), ["b"] = new(0.10), ["c"] = new(0.10), ["owner"] = new(0.55) },
            Quiet = Rig.Night,
        };
        var ledger = new Ledger(policy, clock);

        var jobs = new List<Job>();
        for (var i = 0; i < Consumers.Length; i++)
        {
            jobs.AddRange(MakeJobs(rng, Consumers[i], Targets[i], 120, start, daytimeOnly: false, 1.0, weightError));
        }

        jobs.AddRange(MakeJobs(rng, "owner", 0.45, 300, start, daytimeOnly: true, 1.0 - ownerUnreported, weightError));

        var events = new List<(DateTimeOffset At, int Order, Job? Report)>();
        foreach (var job in jobs)
        {
            if (job.Reported)
            {
                var at = job.End + TimeSpan.FromSeconds(rng.NextDouble() * 30);
                events.Add((at, 1, job));
                events.Add((at + TimeSpan.FromMinutes(1), 0, null));
            }
        }

        for (var t = start; t < reset; t += TimeSpan.FromMinutes(30))
        {
            events.Add((t, 0, null));
        }

        var truth = new Dictionary<string, double>();
        var sorted = jobs.OrderBy(j => j.End).ToList();
        var accrued = 0.0;
        var next = 0;
        ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.0, reset, ObservedAt: start));
        foreach (var (at, _, report) in events.OrderBy(e => e.At).ThenBy(e => e.Order))
        {
            if (at >= reset)
            {
                break;
            }

            clock.SetUtcNow(at);
            while (next < sorted.Count && sorted[next].End <= at)
            {
                accrued += sorted[next].Points;
                truth[sorted[next].Consumer] = truth.GetValueOrDefault(sorted[next].Consumer) + sorted[next].Points;
                next++;
            }

            if (report is not null)
            {
                ledger.Report(new UsageReport(report.Consumer, new Dictionary<string, double> { ["m"] = report.ReportedUsd }));
            }
            else
            {
                ledger.Observe(new MeterReading(MeterWindow.SevenDay, Math.Floor(accrued / 0.01) * 0.01, reset, ObservedAt: at));
            }
        }

        var status = ledger.Status().Consumers;
        return Consumers.Select(c => (c, Math.Abs(status[c].Used - truth.GetValueOrDefault(c)) * 100)).ToList();
    }

    private static List<Job> MakeJobs(Random rng, string consumer, double totalPoints, int count, DateTimeOffset start, bool daytimeOnly, double reportedFraction, double weightError)
    {
        var weights = Enumerable.Range(0, count).Select(_ => 0.2 + (1.6 * rng.NextDouble())).ToArray();
        var scale = totalPoints / weights.Sum();
        var jobs = new List<Job>(count);
        for (var i = 0; i < count; i++)
        {
            var minute = daytimeOnly
                ? (rng.Next(7) * 24 * 60) + (8 * 60) + rng.Next(16 * 60)
                : rng.Next(7 * 24 * 60);
            var points = weights[i] * scale;
            var weight = 1 + (weightError * ((2 * rng.NextDouble()) - 1));
            jobs.Add(new Job(consumer, start.AddMinutes(minute), points, (points / PointsPerDollar) * weight, rng.NextDouble() < reportedFraction));
        }

        return jobs;
    }

    private sealed record Job(string Consumer, DateTimeOffset End, double Points, double ReportedUsd, bool Reported);
}
