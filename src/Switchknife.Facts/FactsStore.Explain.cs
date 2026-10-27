using Npgsql;

namespace Switchknife.Facts;

public sealed partial class FactsStore
{
    private const int MaxSuccessors = 32;

    private const string FactColumns =
        "f.id, f.subject_id, f.predicate, f.object_value, f.text, lower(f.valid), upper(f.valid), f.recorded_at, f.object_id, f.retired_at, f.retired_reason, f.superseded_by";

    private const string ExplainFactSql = "select " + FactColumns + ", f.fingerprint from facts.facts f where f.namespace = $1 and f.id = $2";

    private const string ExplainSourcesSql =
        """
        select distinct e.recorded_at, e.id, s.locator::text, e.source_kind, e.source_ref, e.content_hash, s.quote, e.trust, e.occurred_at
        from facts.fact_sources s join facts.episodes e on e.id = s.episode_id
        where s.fact_id = $2 and e.namespace = $1 order by e.recorded_at, e.id, s.locator::text
        """;

    private const string ExplainRetractionSql =
        """
        select r.retracted_at, r.restored_at from facts.retractions r
        where r.namespace = $1 and r.fingerprint = (select fingerprint from facts.facts where namespace = $1 and id = $2)
        order by r.retracted_at desc limit 1
        """;

    // The chain stops at a repeated id (a cycle) and at MaxSuccessors rows.
    private const string ExplainSuccessorsSql =
        """
        with recursive chain (id, depth, seen) as (
            select s.superseded_by, 1, array[s.id, s.superseded_by] from facts.facts s where s.namespace = $1 and s.id = $2 and s.superseded_by is not null
            union all
            select n.superseded_by, c.depth + 1, c.seen || n.superseded_by
            from chain c join facts.facts n on n.id = c.id
            where n.superseded_by is not null and n.superseded_by <> all (c.seen) and c.depth < 32
        )
        select
        """ + " " + FactColumns + """

        from chain c join facts.facts f on f.id = c.id order by c.depth
        """;

    // A replaced row shares its subject and predicate, so the subject index finds it.
    private const string ExplainPredecessorsSql =
        "select " + FactColumns + """

        from facts.facts me join facts.facts f on f.namespace = me.namespace and f.subject_id = me.subject_id and f.predicate = me.predicate
        where me.namespace = $1 and me.id = $2 and f.superseded_by = me.id order by f.recorded_at, f.id
        """;

    /// <summary>
    /// Why a stored row is believed: its sources (episode, locator, quote), its trust, whether its assertion was retracted, and
    /// the rows that replaced it or that it replaced. One round trip. The text of a source stays with the consumer; the locator
    /// is what its resolver turns into a link or a snippet.
    /// </summary>
    /// <param name="factId">A row id, as returned by <see cref="AsOfAsync"/>, <see cref="HistoryAsync"/> or a search.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The explanation, or null when no row of this namespace has that id (never written, or purged).</returns>
    public async Task<FactExplanation?> ExplainAsync(Guid factId, CancellationToken cancellationToken = default)
    {
        var batch = _data.CreateBatch();
        await using var batchScope = batch.ConfigureAwait(false);
        foreach (var sql in new[] { ExplainFactSql, ExplainSourcesSql, ExplainRetractionSql, ExplainSuccessorsSql, ExplainPredecessorsSql })
        {
            Add(batch, sql, Text(_namespace), Uuid(factId));
        }

        var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = ReadVersion(reader);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var sources = new List<FactSource>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(new FactSource(
                reader.GetGuid(1), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(2),
                reader.IsDBNull(6) ? null : reader.GetString(6), ParseTrust(reader.GetString(7)), reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(0)));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var retraction = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FactRetraction(reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1))
            : null;
        var successors = await ReadVersionsAsync(reader, cancellationToken).ConfigureAwait(false);
        var predecessors = await ReadVersionsAsync(reader, cancellationToken).ConfigureAwait(false);
        var trust = sources.Count == 0 ? EpisodeTrust.ThirdParty : sources.Max(s => s.Trust);
        return new FactExplanation(version, sources, trust, retraction, successors, predecessors);
    }

    private static async Task<List<FactVersion>> ReadVersionsAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var versions = new List<FactVersion>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    private static EpisodeTrust ParseTrust(string text) => text switch
    {
        "owner" => EpisodeTrust.Owner,
        "agent" => EpisodeTrust.Agent,
        "third_party" => EpisodeTrust.ThirdParty,
        _ => throw new InvalidOperationException($"Unknown trust '{text}' in facts.episodes."),
    };
}
