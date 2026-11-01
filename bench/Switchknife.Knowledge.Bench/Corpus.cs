using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Switchknife.Facts;

namespace Switchknife.Knowledge.Bench;

internal sealed record CorpusSource(string Kind, string Ref, EpisodeTrust Trust, DateTimeOffset OccurredAt);

internal sealed record CorpusFact(string Predicate, string Text, string SubjectKind, string SubjectName, string? SubjectRef, string? ObjectName, string? ObjectKind, string? ObjectRef, string? Value, CorpusSource Source);

/// <summary>A namespace's facts as one JSON object per line, as exported from a database that holds the namespace.</summary>
internal static class Corpus
{
    public static IReadOnlyList<CorpusFact> Load(string path) => File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(line =>
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        var subject = r.GetProperty("subject");
        var source = r.GetProperty("sources")[0];
        var o = r.GetProperty("object");
        var hasObject = o.ValueKind == JsonValueKind.Object;
        return new CorpusFact(
            r.GetProperty("predicate").GetString()!, r.GetProperty("text").GetString()!, subject.GetProperty("kind").GetString()!, subject.GetProperty("name").GetString()!,
            Str(subject, "ref"), hasObject ? o.GetProperty("name").GetString() : null, hasObject ? o.GetProperty("kind").GetString() : null, hasObject ? Str(o, "ref") : null,
            r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
            new CorpusSource(
                source.GetProperty("kind").GetString()!, source.GetProperty("ref").GetString()!,
                source.GetProperty("trust").GetString() switch { "third_party" => EpisodeTrust.ThirdParty, "agent" => EpisodeTrust.Agent, _ => EpisodeTrust.Owner },
                source.GetProperty("occurred_at").GetDateTimeOffset()));
    }).ToList();

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Writes <paramref name="facts"/> into <paramref name="ns"/>, one episode per source. With <paramref name="copies"/> above 1 the set is
    /// tiled: copy n renames each subject with a suffix and adds the same suffix to the fact text, so rows stay distinct and the vocabulary stays real.
    /// </summary>
    public static async Task LoadAsync(NpgsqlDataSource data, string ns, IReadOnlyList<CorpusFact> facts, int copies, int? maxFacts = null)
    {
        var store = new FactsStore(data, ns);
        foreach (var predicate in facts.Select(f => f.Predicate).Distinct())
        {
            await store.DefinePredicateAsync(predicate, Cardinality.Many).ConfigureAwait(false);
        }

        var written = 0;
        var entities = new Dictionary<(string, string, string?), Guid>();
        async Task<Guid> EntityAsync(string kind, string name, string? reference)
        {
            if (!entities.TryGetValue((kind, name, reference), out var id))
            {
                id = await store.EnsureEntityAsync(kind, name, reference).ConfigureAwait(false);
                entities[(kind, name, reference)] = id;
            }

            return id;
        }

        for (var copy = 0; copy < copies; copy++)
        {
            var suffix = copy == 0 ? "" : $" v{copy}";
            foreach (var group in facts.GroupBy(f => (f.Source.Kind, f.Source.Ref)))
            {
                var first = group.First().Source;
                var batch = new List<NewFact>();
                foreach (var f in group)
                {
                    if (maxFacts is { } cap && written >= cap)
                    {
                        break;
                    }

                    var subject = await EntityAsync(f.SubjectKind, f.SubjectName + suffix, f.SubjectRef is null ? null : f.SubjectRef + suffix).ConfigureAwait(false);
                    Guid? obj = f.ObjectName is null ? null : await EntityAsync(f.ObjectKind!, f.ObjectName + suffix, f.ObjectRef is null ? null : f.ObjectRef + suffix).ConfigureAwait(false);
                    var text = f.Text.Length + suffix.Length > 300 ? f.Text[..(300 - suffix.Length)] + suffix : f.Text + suffix;
                    batch.Add(new NewFact(subject, f.Predicate, obj is null ? (f.Value ?? f.Text) + suffix : null, first.OccurredAt, Text: text, ObjectEntity: obj));
                    written++;
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                var reference = group.Key.Ref + suffix;
                await store.IngestAsync(new NewEpisode(group.Key.Kind, reference, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))), first.OccurredAt, first.Trust), batch).ConfigureAwait(false);
            }
        }
    }
}
