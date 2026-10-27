namespace Switchknife.Facts.Bench;

/// <summary>
/// The thresholds a run must meet: spec checks G1 (ingest), G2 (as-of), G3 (nearest), F3d (fusion) and G4 (footprint) in
/// <c>docs/specs/2026-10-26-facts-library.md</c>, and G3r (tie-aware recall of the quantized nearest search,
/// <c>docs/specs/2026-10-27-facts-vectors.md</c>); fusion is <c>docs/specs/2026-10-29-facts-explain-fusion.md</c>. They are the ADR's targets taken as written; a change is an ADR line.
/// </summary>
internal sealed record Gates(double IngestP95Ms, double AsOfP95Ms, double NearestP95Ms, double FusionP95Ms, double FactsMb, double EmbeddingMb, double NearestTieRecall)
{
    public static readonly Gates Adr = new(IngestP95Ms: 30, AsOfP95Ms: 10, NearestP95Ms: 50, FusionP95Ms: 100, FactsMb: 150, EmbeddingMb: 100, NearestTieRecall: 0.95);

    private const double Mb = 1024 * 1024;

    /// <summary>One line per exceeded threshold, naming the report field; empty when the run passes. Nearest and embedding limits apply only to a run that measured them.</summary>
    public IReadOnlyList<string> Check(RunReport report)
    {
        var failures = new List<string>();
        Over(failures, "ingestPerEpisode.p95", report.IngestPerEpisode.P95, IngestP95Ms, "ms");
        Over(failures, "asOf.p95", report.AsOf.P95, AsOfP95Ms, "ms");
        Over(failures, "factsBytes", report.FactsBytes / Mb, FactsMb, "MB");
        if (report.Fusion is { } fusion)
        {
            Over(failures, "fusion.p95", fusion.P95, FusionP95Ms, "ms");
        }

        if (report.Nearest is { } nearest)
        {
            Over(failures, "nearest.p95", nearest.P95, NearestP95Ms, "ms");
            Over(failures, "embeddingBytes", report.EmbeddingBytes / Mb, EmbeddingMb, "MB");
            if (!(report.NearestTieRecall >= NearestTieRecall))
            {
                failures.Add($"nearestTieRecall is {report.NearestTieRecall:F3}, under the limit of {NearestTieRecall:F2}");
            }
        }

        return failures;
    }

    private static void Over(List<string> failures, string field, double value, double limit, string unit)
    {
        // NaN (no samples) is a failure too: a run that measured nothing proves nothing.
        if (!(value <= limit))
        {
            failures.Add($"{field} is {value:F1} {unit}, over the limit of {limit:F0} {unit}");
        }
    }
}
