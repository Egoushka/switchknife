namespace Switchknife.Facts.Bench;

/// <summary>Latency percentiles by nearest rank: the value a given share of samples is at or below.</summary>
internal static class Stats
{
    public static double Percentile(IReadOnlyList<double> samples, double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);
        if (samples.Count == 0)
        {
            return double.NaN;
        }

        var sorted = samples.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length);
        return sorted[Math.Max(rank, 1) - 1];
    }

    public static Latency Summarize(IReadOnlyList<double> milliseconds) =>
        new(milliseconds.Count, Percentile(milliseconds, 50), Percentile(milliseconds, 95), Percentile(milliseconds, 99));
}

/// <summary>Milliseconds at the 50th, 95th and 99th percentile of <paramref name="Count"/> samples.</summary>
internal sealed record Latency(int Count, double P50, double P95, double P99);
