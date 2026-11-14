using Npgsql;

namespace Switchknife.Facts;

public sealed partial class FactsStore
{
    private const string SupersedeLoadSql =
        """
        select f.id, f.subject_id, f.retired_at, f.retired_reason, f.superseded_by, p.cardinality
        from facts.facts f join facts.predicates p on p.namespace = f.namespace and p.name = f.predicate
        where f.namespace = $1 and f.id = any($2) order by f.id for update of f
        """;

    private const string SupersedeSql =
        "update facts.facts set retired_at = $2, retired_reason = 'superseded', superseded_by = $3 where id = $1 and retired_at is null";

    /// <summary>
    /// Closes a fact by naming the fact that replaces it: <paramref name="oldFactId"/> is retired at the store's clock with reason
    /// <c>superseded</c> and carries <paramref name="newFactId"/>. Nothing is deleted; the old row stays in <see cref="HistoryAsync"/>
    /// and <see cref="ExplainAsync"/> shows the chain. This is how a <see cref="Cardinality.Many"/> fact is replaced; a
    /// <see cref="Cardinality.One"/> predicate supersedes by rule on write.
    /// </summary>
    /// <param name="oldFactId">The live row to close.</param>
    /// <param name="newFactId">The live row that replaces it, about the same subject.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the old row was closed; <see langword="false"/> when it was already superseded by <paramref name="newFactId"/>.</returns>
    /// <exception cref="ArgumentException">The two ids are the same.</exception>
    /// <exception cref="InvalidOperationException">A row is unknown, retired (the old one by anything but this successor), about another subject, or an event.</exception>
    public async Task<bool> SupersedeAsync(Guid oldFactId, Guid newFactId, CancellationToken cancellationToken = default)
    {
        if (oldFactId == newFactId)
        {
            throw new ArgumentException("A fact cannot supersede itself.", nameof(newFactId));
        }

        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        var rows = new Dictionary<Guid, (Guid Subject, bool Retired, Guid? SupersededBy, string Cardinality)>();
        var load = new NpgsqlCommand(SupersedeLoadSql, connection, transaction);
        await using (load.ConfigureAwait(false))
        {
            load.Parameters.Add(Text(_namespace));
            load.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, Value = new[] { oldFactId, newFactId } });
            var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows[reader.GetGuid(0)] = (reader.GetGuid(1), !reader.IsDBNull(2), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetString(5));
            }
        }

        if (!rows.TryGetValue(oldFactId, out var old))
        {
            throw new InvalidOperationException($"Fact {oldFactId} is not in namespace '{_namespace}'.");
        }

        if (!rows.TryGetValue(newFactId, out var replacement))
        {
            throw new InvalidOperationException($"Fact {newFactId} is not in namespace '{_namespace}'.");
        }

        if (old is { Retired: true, SupersededBy: { } by } && by == newFactId)
        {
            return false;
        }

        if (old.Retired)
        {
            throw new InvalidOperationException($"Fact {oldFactId} is already retired.");
        }

        if (replacement.Retired)
        {
            throw new InvalidOperationException($"Fact {newFactId} is retired and cannot replace anything.");
        }

        if (old.Subject != replacement.Subject)
        {
            throw new InvalidOperationException("A fact can only be superseded by a fact about the same subject.");
        }

        if (old.Cardinality == ToText(Cardinality.Event) || replacement.Cardinality == ToText(Cardinality.Event))
        {
            throw new InvalidOperationException("An event is a point in time and is never superseded.");
        }

        var update = new NpgsqlCommand(SupersedeSql, connection, transaction);
        await using (update.ConfigureAwait(false))
        {
            update.Parameters.Add(Uuid(oldFactId));
            update.Parameters.Add(Time(Micro(_time.GetUtcNow())));
            update.Parameters.Add(Uuid(newFactId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>What the store believes now about <paramref name="subject"/>: <see cref="AsOfAsync"/> at the store's clock on both timelines.</summary>
    /// <param name="subject">The entity.</param>
    /// <param name="predicate">One predicate, or null for all of them.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public Task<IReadOnlyList<Fact>> CurrentAsync(Guid subject, string? predicate = null, CancellationToken cancellationToken = default) =>
        AsOfAsync(subject, predicate, _time.GetUtcNow(), cancellationToken: cancellationToken);
}
