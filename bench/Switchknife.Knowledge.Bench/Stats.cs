namespace Switchknife.Knowledge.Bench;

internal static class Stats
{
    /// <summary>Wilson 95% interval for <paramref name="found"/> of <paramref name="total"/>.</summary>
    public static (double Low, double High) Wilson(int found, int total)
    {
        const double z = 1.959964;
        var p = (double)found / total;
        var denominator = 1 + z * z / total;
        var centre = (p + z * z / (2 * total)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / total + z * z / (4.0 * total * total)) / denominator;
        return (centre - margin, centre + margin);
    }

    /// <summary>The value at the 95th percentile by nearest rank.</summary>
    public static double P95(IReadOnlyList<double> sorted) => sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
}
