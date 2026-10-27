using System.Globalization;

namespace Switchknife.Facts.Bench;

/// <summary>One thing learned: <paramref name="Value"/> of a single-valued predicate began on <paramref name="ValidFrom"/>, and the store heard of it at <paramref name="RecordedAt"/>.</summary>
internal sealed record Learned(Guid Subject, string Predicate, string Value, DateTime ValidFrom, DateTime RecordedAt);

/// <summary>A many-valued fact (<c>knows</c>): it never supersedes anything.</summary>
internal sealed record Added(Guid Subject, string Value, DateTime ValidFrom, DateTime RecordedAt);

/// <summary>One ingest: the facts the store hears of together, all recorded at <paramref name="RecordedAt"/>, as a real episode is.</summary>
internal sealed record WorkloadEpisode(DateTime RecordedAt, IReadOnlyList<Learned> Singles, IReadOnlyList<Added> Many);

/// <summary>
/// A deterministic history: people whose city, employer and status change over time, learned in an order that
/// differs from the order they happened in. The oracle answers "what was true at T, as known at K" from the
/// generator's own list, with no database involved, so a store that disagrees is wrong.
/// </summary>
internal sealed class Workload
{
    public static readonly string[] SinglePredicates = ["lives_in", "works_at", "status"];
    public const string ManyPredicate = "knows";
    public static readonly DateTime Origin = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<(Guid, string), List<Learned>> _slots = [];

    public IReadOnlyList<Guid> Subjects { get; }

    /// <summary>The ingests in recorded order; every fact in one carries that ingest's time, since the store stamps a whole episode with its clock.</summary>
    public IReadOnlyList<WorkloadEpisode> Episodes { get; }

    public IReadOnlyList<Learned> Singles { get; }

    public IReadOnlyList<Added> Many { get; }

    public Workload(int subjects, int factsTarget, int seed, int episodeSize = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(episodeSize, 1);
        var random = new Random(seed);
        Subjects = [.. Enumerable.Range(0, subjects).Select(_ => new Guid(Bytes(random)))];
        var manyShare = factsTarget / 5;
        var singleTarget = factsTarget - manyShare;
        var perSlot = Math.Max(1, (int)Math.Ceiling((double)singleTarget / (subjects * SinglePredicates.Length)));

        var singles = new List<Learned>();
        foreach (var subject in Subjects)
        {
            foreach (var predicate in SinglePredicates)
            {
                // Distinct days inside a slot, so two versions never begin together.
                var days = Enumerable.Range(0, 700).OrderBy(_ => random.Next()).Take(perSlot).Order().ToArray();
                var list = new List<Learned>();
                for (var i = 0; i < days.Length; i++)
                {
                    var from = Origin.AddDays(days[i]);
                    var learned = new Learned(subject, predicate, string.Create(CultureInfo.InvariantCulture, $"{predicate.Replace("_", "", StringComparison.Ordinal)}{i}x{random.Next(1000)}"),
                        from, from.AddDays(random.Next(0, 40)).AddHours(random.Next(24)));
                    list.Add(learned);
                }

                singles.AddRange(list);
            }
        }

        var learnedInOrder = singles.OrderBy(l => l.RecordedAt).ThenBy(l => l.ValidFrom).ToList();
        var addedInOrder = Enumerable.Range(0, manyShare).Select(i =>
        {
            var at = Origin.AddDays(random.Next(700));
            return new Added(Subjects[random.Next(Subjects.Count)], string.Create(CultureInfo.InvariantCulture, $"person-{i}"), at, at.AddDays(random.Next(10)));
        }).OrderBy(a => a.RecordedAt).ToList();

        // Learned order is recorded order. Facts are grouped into episodes by time, and an episode is recorded when its last fact arrived.
        var timeline = learnedInOrder.Select(s => (s.RecordedAt, Single: (Learned?)s, Many: (Added?)null))
            .Concat(addedInOrder.Select(m => (m.RecordedAt, Single: (Learned?)null, Many: (Added?)m)))
            .OrderBy(e => e.RecordedAt).ToList();
        Episodes = [.. timeline.Chunk(episodeSize).Select(chunk =>
        {
            var recorded = chunk.Max(e => e.RecordedAt);
            return new WorkloadEpisode(
                recorded,
                [.. chunk.Where(e => e.Single is not null).Select(e => e.Single! with { RecordedAt = recorded })],
                [.. chunk.Where(e => e.Many is not null).Select(e => e.Many! with { RecordedAt = recorded })]);
        })];
        Singles = [.. Episodes.SelectMany(e => e.Singles)];
        Many = [.. Episodes.SelectMany(e => e.Many)];
        foreach (var slot in Singles.GroupBy(l => (l.Subject, l.Predicate)))
        {
            _slots[slot.Key] = [.. slot];
        }
    }

    /// <summary>The value of a slot valid at <paramref name="validAt"/> among what was recorded by <paramref name="knownAt"/>; null when nothing was known then.</summary>
    public string? Expected(Guid subject, string predicate, DateTime validAt, DateTime knownAt) =>
        _slots.TryGetValue((subject, predicate), out var versions)
            ? versions.Where(v => v.RecordedAt <= knownAt && v.ValidFrom <= validAt).MaxBy(v => v.ValidFrom)?.Value
            : null;

    private static byte[] Bytes(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return bytes;
    }
}
