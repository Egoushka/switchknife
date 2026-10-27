using Npgsql;

namespace Switchknife.Facts;

public sealed partial class FactsStore
{
    private const string FindAssertionSql = "select slot, fingerprint from facts.facts where id = $1 and namespace = $2";

    private const string RetractSql =
        "update facts.facts set retired_at = $3, retired_reason = 'retracted' where namespace = $1 and fingerprint = $2 and retired_at is null";

    private const string BlockSql =
        "insert into facts.retractions (namespace, fingerprint, retracted_at) values ($1, $2, $3) on conflict do nothing";

    private const string BlockedAtSql = "select retracted_at from facts.retractions where namespace = $1 and fingerprint = $2";

    private const string RetractedRowsSql =
        "select id from facts.facts where namespace = $1 and fingerprint = $2 and retired_reason = 'retracted' and retired_at = $3 order by lower(valid), id";

    private const string RestoreRowSql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, object_id, text, valid, recorded_at, slot, fingerprint, confidence, episode_id)
        select $1, namespace, subject_id, predicate, object_value, object_id, text, valid, $3, slot, fingerprint, confidence, episode_id
        from facts.facts where id = $2
        """;

    private const string CopyBitsSql =
        "insert into facts.fact_embeddings (fact_id, model, namespace, bits) select $1, model, namespace, bits from facts.fact_embeddings where fact_id = $2";

    private const string CopyVectorsSql =
        "insert into facts.fact_vectors (fact_id, model, q8) select $1, model, q8 from facts.fact_vectors where fact_id = $2";

    private const string UnblockSql = "delete from facts.retractions where namespace = $1 and fingerprint = $2";

    /// <summary>
    /// Retracts an assertion: every live row of it (a clipped or resumed value may be several) is retired with reason
    /// <c>retracted</c>, and its fingerprint is blocked so no later extraction writes it again. On a <see cref="Cardinality.One"/>
    /// slot the interval it held becomes a gap: the value it clipped is not reopened. Retracting twice changes nothing.
    /// </summary>
    /// <param name="factId">Any row of the assertion, live or retired, from this namespace. An unknown id retracts nothing.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of rows retired; 0 when the assertion has no live row.</returns>
    public async Task<int> RetractAsync(Guid factId, CancellationToken cancellationToken = default)
    {
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        if (await LockAssertionAsync(connection, transaction, factId, cancellationToken).ConfigureAwait(false) is not { } fingerprint)
        {
            return 0;
        }

        var now = Micro(_time.GetUtcNow());
        var batch = new NpgsqlBatch(connection, transaction);
        await using var batchScope = batch.ConfigureAwait(false);
        Add(batch, RetractSql, Text(_namespace), Text(fingerprint), Time(now));
        Add(batch, BlockSql, Text(_namespace), Text(fingerprint), Time(now));
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var retired = batch.BatchCommands[0].RecordsAffected;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return retired;
    }

    /// <summary>
    /// Undoes a retraction: lifts the fingerprint's block and writes the rows the retraction retired again as new live rows with
    /// the same text, range, sources and embeddings. The retracted rows stay in the history.
    /// </summary>
    /// <param name="factId">Any row of the retracted assertion.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of rows written; 0 when the assertion is not retracted.</returns>
    /// <exception cref="InvalidOperationException">A newer fact now occupies the interval, or the assertion is live again.</exception>
    public async Task<int> RestoreAsync(Guid factId, CancellationToken cancellationToken = default)
    {
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        if (await LockAssertionAsync(connection, transaction, factId, cancellationToken).ConfigureAwait(false) is not { } fingerprint)
        {
            return 0;
        }

        DateTimeOffset retractedAt;
        var blocked = new NpgsqlCommand(BlockedAtSql, connection, transaction);
        await using (blocked.ConfigureAwait(false))
        {
            blocked.Parameters.Add(Text(_namespace));
            blocked.Parameters.Add(Text(fingerprint));
            var at = await blocked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var atScope = at.ConfigureAwait(false);
            if (!await at.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            retractedAt = at.GetFieldValue<DateTimeOffset>(0);
        }

        var ids = new List<Guid>();
        var rows = new NpgsqlCommand(RetractedRowsSql, connection, transaction);
        await using (rows.ConfigureAwait(false))
        {
            rows.Parameters.Add(Text(_namespace));
            rows.Parameters.Add(Text(fingerprint));
            rows.Parameters.Add(Time(retractedAt));
            var reader = await rows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        var now = Micro(_time.GetUtcNow());
        var batch = new NpgsqlBatch(connection, transaction);
        await using var batchScope = batch.ConfigureAwait(false);
        foreach (var id in ids)
        {
            var restored = Guid.CreateVersion7(now);
            Add(batch, RestoreRowSql, Uuid(restored), Uuid(id), Time(now));
            Add(batch, CopySourcesSql, Uuid(restored), Uuid(id));
            Add(batch, CopyBitsSql, Uuid(restored), Uuid(id));
            Add(batch, CopyVectorsSql, Uuid(restored), Uuid(id));
        }

        Add(batch, UnblockSql, Text(_namespace), Text(fingerprint));
        try
        {
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("The retracted fact cannot be restored: a newer fact occupies its interval, or it is live again.", e);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count;
    }

    /// <summary>Takes the slot lock of the assertion's row (for a <c>one</c> predicate) and returns its fingerprint, or null when the id is not a row of this namespace.</summary>
    private async Task<string?> LockAssertionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid factId, CancellationToken ct)
    {
        string? slot, fingerprint;
        var find = new NpgsqlCommand(FindAssertionSql, connection, transaction);
        await using (find.ConfigureAwait(false))
        {
            find.Parameters.Add(Uuid(factId));
            find.Parameters.Add(Text(_namespace));
            var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            slot = reader.IsDBNull(0) ? null : reader.GetString(0);
            fingerprint = reader.GetString(1);
        }

        if (slot is not null)
        {
            var lockSlot = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction);
            await using var lockScope = lockSlot.ConfigureAwait(false);
            lockSlot.Parameters.Add(Text(slot));
            await lockSlot.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return fingerprint;
    }
}
