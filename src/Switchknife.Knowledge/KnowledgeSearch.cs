using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Switchknife.Facts;

namespace Switchknife.Knowledge;

/// <summary>
/// Retrieval over several <see cref="FactsStore"/> namespaces in one database. Per namespace, in parallel: an idf-weighted
/// OR full-text list (<see cref="FactsStore.SearchAsync"/> ANDs every word, so a question written as a sentence matches
/// nothing), fused with the nearest facts when a vector is given, then grouped by the entity each fact is about. Entity lists
/// of the namespaces are fused by reciprocal rank, <c>1 / (60 + rank)</c>, the constant Facts uses inside one namespace.
/// </summary>
public sealed class KnowledgeSearch
{
    private const int RankConstant = 60;
    private const int FactsPerEntity = 2;

    private readonly NpgsqlDataSource _data;
    private readonly IReadOnlyList<(string Namespace, FactsStore Store)> _namespaces;
    private readonly TimeProvider _time;

    /// <param name="data">The database that holds every namespace.</param>
    /// <param name="namespaces">The namespaces to read; each must exist in the facts schema.</param>
    /// <param name="time">The clock; defaults to the system clock.</param>
    public KnowledgeSearch(NpgsqlDataSource data, IReadOnlyList<string> namespaces, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(namespaces);
        _data = data;
        _time = time ?? TimeProvider.System;
        _namespaces = namespaces.Select(n => (n, new FactsStore(data, n, _time))).ToList();
    }

    /// <summary>
    /// Entities for a question, best first.
    /// </summary>
    /// <param name="question">The question in words; null or blank gives no text list.</param>
    /// <param name="vector">The question's embedding, or empty for words alone.</param>
    /// <param name="model">The embedding model of <paramref name="vector"/>; required with a vector.</param>
    /// <param name="limit">How many entities to return, 1 to 100.</param>
    /// <param name="knownAt">Believed-at instant; defaults to now.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public async Task<IReadOnlyList<EntityHit>> SearchAsync(
        string? question, ReadOnlyMemory<float> vector = default, string? model = null, int limit = 10, DateTimeOffset? knownAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var terms = question is null ? [] : QueryTerms.Of(question);
        if (terms.Count == 0 && vector.IsEmpty)
        {
            return [];
        }

        if (!vector.IsEmpty)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
        }

        var at = knownAt ?? _time.GetUtcNow();
        var perNamespace = await Task.WhenAll(_namespaces.Select(n => OneAsync(n.Namespace, n.Store, terms, vector, model, limit, at, cancellationToken))).ConfigureAwait(false);

        var fused = new Dictionary<(string, Guid), (double Score, EntityHit Hit)>();
        foreach (var list in perNamespace)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var key = (list[rank].Namespace, list[rank].EntityId);
                var add = 1.0 / (RankConstant + rank + 1);
                fused[key] = ((fused.TryGetValue(key, out var seen) ? seen.Score : 0) + add, fused.TryGetValue(key, out var kept) ? kept.Hit : list[rank]);
            }
        }

        return fused.Values.OrderByDescending(v => v.Score).ThenBy(v => v.Hit.EntityId)
            .Take(limit).Select(v => v.Hit with { Score = v.Score }).ToList();
    }

    private async Task<IReadOnlyList<EntityHit>> OneAsync(
        string ns, FactsStore store, IReadOnlyList<string> terms, ReadOnlyMemory<float> vector, string? model, int limit, DateTimeOffset at, CancellationToken ct)
    {
        var depth = Math.Min(limit * 8, 400);
        var lexical = terms.Count == 0 ? [] : await LexicalAsync(ns, terms, depth, at, ct).ConfigureAwait(false);
        IReadOnlyList<ScoredFact> nearest = [];
        if (!vector.IsEmpty)
        {
            nearest = await store.NearestAsync(model!, vector, depth, at, at, cancellationToken: ct).ConfigureAwait(false);
        }

        var facts = new Dictionary<Guid, (double Score, Guid Subject, string Predicate, string Text)>();
        for (var i = 0; i < lexical.Count; i++)
        {
            var f = lexical[i];
            facts[f.Id] = (1.0 / (RankConstant + i + 1), f.Subject, f.Predicate, f.Text);
        }

        for (var i = 0; i < nearest.Count; i++)
        {
            var f = nearest[i].Fact;
            var add = 1.0 / (RankConstant + i + 1);
            facts[f.Id] = facts.TryGetValue(f.Id, out var have) ? (have.Score + add, have.Subject, have.Predicate, have.Text) : (add, f.Subject, f.Predicate, f.Text);
        }

        var entities = facts.GroupBy(kv => kv.Value.Subject)
            .Select(g => (Id: g.Key, Score: g.Sum(kv => kv.Value.Score), Facts: g.OrderByDescending(kv => kv.Value.Score).ThenBy(kv => kv.Key).Take(FactsPerEntity)
                .Select(kv => new EntityFact(kv.Key, kv.Value.Predicate, kv.Value.Text, kv.Value.Score)).ToList()))
            .OrderByDescending(e => e.Score).ThenBy(e => e.Id).Take(limit).ToList();
        if (entities.Count == 0)
        {
            return [];
        }

        var rows = await EntitiesAsync(ns, entities.Select(e => e.Id).ToArray(), ct).ConfigureAwait(false);
        return entities.Where(e => rows.ContainsKey(e.Id)).Select(e =>
        {
            var (kind, name, reference) = rows[e.Id];
            return new EntityHit(ns, e.Id, kind, name, reference, e.Score, e.Facts);
        }).ToList();
    }

    private sealed record Candidate(Guid Id, Guid Subject, string Predicate, string Text);

    /// <summary>Facts holding any term as a word prefix, ranked by the summed idf of the terms they hold.</summary>
    private async Task<IReadOnlyList<Candidate>> LexicalAsync(string ns, IReadOnlyList<string> terms, int depth, DateTimeOffset at, CancellationToken ct)
    {
        const string live = "namespace = $1 and valid @> $2 and recorded_at <= $2 and (retired_at is null or retired_at > $2)";
        var queries = terms.Select(t => t + ":*").ToList();

        var dfSql = new StringBuilder("select count(*)");
        for (var i = 0; i < queries.Count; i++)
        {
            dfSql.Append(CultureInfo.InvariantCulture, $", count(*) filter (where search @@ to_tsquery('simple', ${i + 3}))");
        }

        dfSql.Append(" from facts.facts where ").Append(live);
        var counts = new long[queries.Count + 1];
        var conn = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connScope = conn.ConfigureAwait(false);
        var df = new NpgsqlCommand(dfSql.ToString(), conn);
        await using (df.ConfigureAwait(false))
        {
            df.Parameters.Add(new NpgsqlParameter { Value = ns });
            df.Parameters.Add(new NpgsqlParameter { Value = at, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            foreach (var q in queries)
            {
                df.Parameters.Add(new NpgsqlParameter { Value = q });
            }

            var r = await df.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var rScope = r.ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return [];
            }

            for (var i = 0; i < counts.Length; i++)
            {
                counts[i] = r.GetInt64(i);
            }
        }

        var total = counts[0];
        var weighted = new List<(string Query, double Idf)>();
        for (var i = 0; i < queries.Count; i++)
        {
            if (counts[i + 1] > 0)
            {
                weighted.Add((queries[i], Math.Log(1 + (total - counts[i + 1] + 0.5) / (counts[i + 1] + 0.5))));
            }
        }

        if (weighted.Count == 0)
        {
            return [];
        }

        var sql = new StringBuilder("select id, subject_id, predicate, text from facts.facts where ").Append(live).Append(" and search @@ to_tsquery('simple', $3) order by ");
        for (var i = 0; i < weighted.Count; i++)
        {
            sql.Append(i == 0 ? "" : " + ").Append(CultureInfo.InvariantCulture, $"(case when search @@ to_tsquery('simple', ${i + 5}) then ${i + 5 + weighted.Count}::float8 else 0 end)");
        }

        sql.Append(" desc, id limit $4");
        var cmd = new NpgsqlCommand(sql.ToString(), conn);
        await using var cmdScope = cmd.ConfigureAwait(false);
        cmd.Parameters.Add(new NpgsqlParameter { Value = ns });
        cmd.Parameters.Add(new NpgsqlParameter { Value = at, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.Join(" | ", weighted.Select(w => w.Query)) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = depth });
        foreach (var w in weighted)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = w.Query });
        }

        foreach (var w in weighted)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = w.Idf });
        }

        var found = new List<Candidate>();
        var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add(new Candidate(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        }

        return found;
    }

    private async Task<Dictionary<Guid, (string Kind, string Name, string? ExternalRef)>> EntitiesAsync(string ns, Guid[] ids, CancellationToken ct)
    {
        var cmd = _data.CreateCommand("select id, kind, name, external_ref from facts.entities where namespace = $1 and id = any($2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = ns });
        cmd.Parameters.Add(new NpgsqlParameter { Value = ids });
        await using var cmdScope = cmd.ConfigureAwait(false);
        var rows = new Dictionary<Guid, (string, string, string?)>();
        var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows[reader.GetGuid(0)] = (reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        return rows;
    }
}
