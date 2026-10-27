using System.Collections;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Switchknife.Facts;

/// <summary>What a purge removed.</summary>
/// <param name="Episodes">Episodes deleted (every version of the source).</param>
/// <param name="Assertions">Assertions that no longer exist in any row: those only the source supported.</param>
/// <param name="SlotsRebuilt">Single-valued slots (subject and predicate) rewritten by replay.</param>
public sealed record PurgeResult(int Episodes, int Assertions, int SlotsRebuilt);

public sealed partial class FactsStore
{
    private const string EpisodesOfSourceSql = "select id from facts.episodes where namespace = $1 and source_kind = $2 and source_ref = $3";

    private const string AffectedFingerprintsSql =
        """
        select distinct fingerprint from facts.facts
        where namespace = $1 and (episode_id = any($2) or id in (select fact_id from facts.fact_sources where episode_id = any($2)))
        """;

    private const string AffectedSlotsSql =
        """
        select distinct slot from facts.facts
        where namespace = $1 and slot is not null
          and (episode_id = any($2) or id in (select fact_id from facts.fact_sources where episode_id = any($2)))
        order by slot
        """;

    private const string RepointSql =
        """
        update facts.facts f set (episode_id, recorded_at) = (
            select e.id, e.recorded_at from facts.fact_sources x join facts.episodes e on e.id = x.episode_id
            where x.fact_id = f.id and x.episode_id <> all($2) order by e.recorded_at, e.id limit 1)
        where f.namespace = $1 and f.slot is null and f.episode_id = any($2)
          and exists (select 1 from facts.fact_sources x where x.fact_id = f.id and x.episode_id <> all($2))
        """;

    private const string DeleteSourcesOfEpisodesSql = "delete from facts.fact_sources where episode_id = any($1)";

    private const string DeleteUnsupportedBitsSql =
        "delete from facts.fact_embeddings where fact_id in (select id from facts.facts where namespace = $1 and slot is null and episode_id = any($2))";

    private const string DeleteUnsupportedVectorsSql =
        "delete from facts.fact_vectors where fact_id in (select id from facts.facts where namespace = $1 and slot is null and episode_id = any($2))";

    private const string DeleteUnsupportedSql = "delete from facts.facts where namespace = $1 and slot is null and episode_id = any($2)";

    private const string DeleteOrphanBlocksSql =
        """
        delete from facts.retractions r where r.namespace = $1 and r.fingerprint = any($2)
          and not exists (select 1 from facts.facts f where f.namespace = r.namespace and f.fingerprint = r.fingerprint)
        """;

    private const string CountGoneSql =
        "select count(*) from unnest($2::text[]) as fp where not exists (select 1 from facts.facts where namespace = $1 and fingerprint = fp)";

    private const string DeleteEpisodesSql = "delete from facts.episodes where id = any($1)";

    private const string SlotRowsSql =
        """
        select id, fingerprint, subject_id, predicate, object_value, object_id, text, lower(valid), upper(valid), recorded_at, retired_at, retired_reason,
            episode_id, superseded_by is not null
        from facts.facts where namespace = $1 and slot = $2
        """;

    private const string SlotSourcesSql =
        """
        select s.fact_id, s.episode_id, s.locator::text, s.quote, s.assertion, s.assertion_from, s.assertion_to, s.assertion_value, s.assertion_entity, s.assertion_text
        from facts.fact_sources s join facts.facts f on f.id = s.fact_id where f.namespace = $1 and f.slot = $2
        """;

    private const string SlotRetractionsSql =
        "select fingerprint, retracted_at, restored_at from facts.retractions where namespace = $1 and fingerprint = any($2)";

    private const string SlotEmbeddingsSql =
        """
        select distinct on (f.fingerprint, b.model) f.fingerprint, b.model, b.namespace, b.bits, v.q8
        from facts.facts f join facts.fact_embeddings b on b.fact_id = f.id left join facts.fact_vectors v on v.fact_id = f.id and v.model = b.model
        where f.namespace = $1 and f.slot = $2
        order by f.fingerprint, b.model, f.recorded_at
        """;

    private const string EpisodeInfoSql =
        """
        select e.id, e.recorded_at, e.trust, e.source_kind, e.source_ref, exists (
            select 1 from facts.episodes o
            where o.namespace = e.namespace and o.source_kind = e.source_kind and o.source_ref = e.source_ref and (o.recorded_at, o.id) < (e.recorded_at, e.id))
        from facts.episodes e where e.namespace = $1 and (e.id = any($2) or e.recorded_at = any($3))
        """;

    private const string DeleteSlotBitsSql =
        "delete from facts.fact_embeddings where fact_id in (select id from facts.facts where namespace = $1 and slot = $2)";

    private const string DeleteSlotVectorsSql =
        "delete from facts.fact_vectors where fact_id in (select id from facts.facts where namespace = $1 and slot = $2)";

    private const string DeleteSlotSourcesSql =
        "delete from facts.fact_sources where fact_id in (select id from facts.facts where namespace = $1 and slot = $2)";

    private const string DeleteSlotSql = "delete from facts.facts where namespace = $1 and slot = $2";

    private const string DeleteBlocksSql = "delete from facts.retractions where namespace = $1 and fingerprint = any($2)";

    private const string NewRowsOfFingerprintSql = "select id from facts.facts where namespace = $1 and slot = $2 and fingerprint = $3";

    private const string InsertBitsSql = "insert into facts.fact_embeddings (fact_id, model, namespace, bits) values ($1, $2, $3, $4)";

    private const string InsertVectorSql = "insert into facts.fact_vectors (fact_id, model, q8) values ($1, $2, $3)";

    /// <summary>
    /// Deletes a source for privacy: every version of it (every episode with that kind and reference), the facts only it supports
    /// and their embeddings, and the source lines of the facts it only corroborated. Each single-valued slot it touched is then
    /// rewritten by replaying what remains, in the order it was recorded, through the write path itself, so the slot equals one
    /// that never saw the source. Rows of a rewritten slot get new ids; embeddings follow the assertion. One transaction, under
    /// the slot locks. This is the one operation that deletes rows.
    /// </summary>
    /// <param name="sourceKind">The source kind of the episodes.</param>
    /// <param name="sourceRef">The consumer's id of the source.</param>
    /// <param name="cancellationToken">Cancels the call; the transaction rolls back.</param>
    public async Task<PurgeResult> PurgeSourceAsync(string sourceKind, string sourceRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        var episodes = await ReadGuidsAsync(connection, transaction, EpisodesOfSourceSql, [Text(_namespace), Text(sourceKind), Text(sourceRef)], cancellationToken).ConfigureAwait(false);
        if (episodes.Length == 0)
        {
            return new PurgeResult(0, 0, 0);
        }

        NpgsqlParameter Ids() => Uuids(episodes);
        var fingerprints = await ReadStringsAsync(connection, transaction, AffectedFingerprintsSql, [Text(_namespace), Ids()], cancellationToken).ConfigureAwait(false);
        var slots = await ReadStringsAsync(connection, transaction, AffectedSlotsSql, [Text(_namespace), Ids()], cancellationToken).ConfigureAwait(false);
        await LockSlotsAsync(connection, transaction, slots, cancellationToken).ConfigureAwait(false);

        var snapshots = new List<SlotSnapshot>();
        foreach (var slot in slots)
        {
            snapshots.Add(await ReadSlotAsync(connection, transaction, slot, episodes, cancellationToken).ConfigureAwait(false));
        }

        // Facts outside single-valued slots: the ones only the source supports go; the others keep their other sources.
        await ExecuteAsync(connection, transaction, RepointSql, [Text(_namespace), Ids()], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, DeleteSourcesOfEpisodesSql, [Ids()], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, DeleteUnsupportedBitsSql, [Text(_namespace), Ids()], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, DeleteUnsupportedVectorsSql, [Text(_namespace), Ids()], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, DeleteUnsupportedSql, [Text(_namespace), Ids()], cancellationToken).ConfigureAwait(false);

        foreach (var snapshot in snapshots)
        {
            await ReplayAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        }

        NpgsqlParameter Fingerprints() => new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = fingerprints };
        await ExecuteAsync(connection, transaction, DeleteOrphanBlocksSql, [Text(_namespace), Fingerprints()], cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, DeleteEpisodesSql, [Ids()], cancellationToken).ConfigureAwait(false);
        var gone = await ScalarAsync(connection, transaction, CountGoneSql, [Text(_namespace), Fingerprints()], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PurgeResult(episodes.Length, (int)(long)gone!, slots.Length);
    }

    /// <summary>
    /// Replays a single-valued slot's own history through the write path inside a transaction and reports where the replay differs
    /// from the stored rows, then rolls the transaction back, so nothing is written. Empty means the stored rows are exactly what the
    /// recorded episodes, retractions and source changes produce: the integrity check behind <see cref="PurgeSourceAsync"/>.
    /// </summary>
    /// <param name="subject">The entity.</param>
    /// <param name="predicate">A predicate of cardinality <see cref="Cardinality.One"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One line per row that is stored but not replayed (<c>stored</c>) or replayed but not stored (<c>replayed</c>).</returns>
    public async Task<IReadOnlyList<string>> VerifySlotAsync(Guid subject, string predicate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        var slot = string.Create(CultureInfo.InvariantCulture, $"{subject}/{predicate}");
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        await LockSlotsAsync(connection, transaction, [slot], cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadSlotAsync(connection, transaction, slot, [], cancellationToken).ConfigureAwait(false);
        var stored = Describe(snapshot);
        await ReplayAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        var replayed = Describe(await ReadSlotAsync(connection, transaction, slot, [], cancellationToken).ConfigureAwait(false));
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return [.. stored.Except(replayed).Select(d => "stored: " + d), .. replayed.Except(stored).Select(d => "replayed: " + d)];
    }

    /// <summary>One line per row (without ids) and per retraction, so two states compare as sets of text.</summary>
    private static List<string> Describe(SlotSnapshot snapshot)
    {
        var sources = snapshot.AllSources.ToLookup(s => s.FactId);
        var lines = new List<string>();
        foreach (var row in snapshot.AllRows)
        {
            var listed = sources[row.Id].Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.EpisodeId}:{s.Locator}:{s.Quote}")).Order(StringComparer.Ordinal);
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Fingerprint}|{row.Lower:O}|{row.Upper:O}|{row.RecordedAt:O}|{row.RetiredAt:O}|{row.Reason}|{row.Text}|{row.Superseded}|{row.EpisodeId}|{string.Join(';', listed)}"));
        }

        foreach (var block in snapshot.AllRetractions)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"retraction|{block.Fingerprint}|{block.RetractedAt:O}|{block.RestoredAt:O}"));
        }

        return lines;
    }

    private async Task<SlotSnapshot> ReadSlotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string slot, Guid[] purged, CancellationToken ct)
    {
        var rows = new List<SlotRow>();
        var sources = new List<SlotSource>();
        var embeddings = new List<SlotEmbedding>();
        var read = new NpgsqlBatch(connection, transaction);
        await using (read.ConfigureAwait(false))
        {
            Add(read, SlotRowsSql, Text(_namespace), Text(slot));
            Add(read, SlotSourcesSql, Text(_namespace), Text(slot));
            Add(read, SlotEmbeddingsSql, Text(_namespace), Text(slot));
            var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new SlotRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetFieldValue<DateTimeOffset>(9), reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetGuid(12), reader.GetBoolean(13)));
            }

            await reader.NextResultAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                sources.Add(new SlotSource(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            await reader.NextResultAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                embeddings.Add(new SlotEmbedding(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<BitArray>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4)));
            }
        }

        var fingerprints = rows.Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal).ToArray();
        var retractions = new List<SlotRetraction>();
        var blocks = new NpgsqlCommand(SlotRetractionsSql, connection, transaction);
        await using (blocks.ConfigureAwait(false))
        {
            blocks.Parameters.Add(Text(_namespace));
            blocks.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = fingerprints });
            var reader = await blocks.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                retractions.Add(new SlotRetraction(
                    reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        // Episodes the surviving sources name, and the ones whose ingest retired rows of this slot as a changed source.
        var episodeIds = sources.Select(s => s.EpisodeId).Distinct().ToArray();
        var changedAt = rows.Where(r => r.Reason == "source_changed").Select(r => r.RetiredAt!.Value).Distinct().ToArray();
        var episodes = new Dictionary<Guid, EpisodeInfo>();
        var info = new NpgsqlCommand(EpisodeInfoSql, connection, transaction);
        await using (info.ConfigureAwait(false))
        {
            info.Parameters.Add(Text(_namespace));
            info.Parameters.Add(Uuids(episodeIds));
            info.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, Value = changedAt.Select(t => t.ToUniversalTime()).ToArray() });
            var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var trust = reader.GetString(2) switch { "owner" => 2, "agent" => 1, _ => 0 };
                episodes[reader.GetGuid(0)] = new EpisodeInfo(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), trust, reader.GetString(3), reader.GetString(4), reader.GetBoolean(5));
            }
        }

        var gone = purged.ToHashSet();
        return new SlotSnapshot(slot, rows, sources, retractions, embeddings, episodes, gone);
    }

    /// <summary>Deletes the slot's rows and writes them again from the surviving sources, in recorded order, through the write path.</summary>
    private async Task ReplayAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SlotSnapshot snapshot, CancellationToken ct)
    {
        var timeline = Timeline(snapshot);
        var allFingerprints = snapshot.AllRows.Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal).ToArray();
        var fingerprintArray = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = allFingerprints };
        foreach (var sql in new[] { DeleteSlotBitsSql, DeleteSlotVectorsSql, DeleteSlotSourcesSql, DeleteSlotSql })
        {
            await ExecuteAsync(connection, transaction, sql, [Text(_namespace), Text(snapshot.Slot)], ct).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, DeleteBlocksSql, [Text(_namespace), fingerprintArray], ct).ConfigureAwait(false);

        foreach (var step in timeline)
        {
            switch (step)
            {
                case IngestStep ingest:
                    await WriteOneAsync(connection, transaction, ingest.Episode.Id, ingest.Episode.Trust, ingest.Episode.RecordedAt, ingest.Fact, ct).ConfigureAwait(false);
                    break;
                case ChangedStep changed:
                    await RetireChangedAsync(connection, transaction, changed.Episode.SourceKind, changed.Episode.SourceRef, changed.Episode.Id, changed.Episode.RecordedAt, ct).ConfigureAwait(false);
                    break;
                case RetractStep retract:
                    await RetractFingerprintAsync(connection, transaction, retract.Fingerprint, retract.At, ct).ConfigureAwait(false);
                    break;
                case RestoreStep restore:
                    await RestoreFingerprintAsync(connection, transaction, restore.Fingerprint, restore.At, ct).ConfigureAwait(false);
                    break;
            }
        }

        // An embedding belongs to the assertion's text, so every row of the assertion gets it again.
        foreach (var embedding in snapshot.Embeddings)
        {
            var rows = await ReadGuidsAsync(connection, transaction, NewRowsOfFingerprintSql, [Text(_namespace), Text(snapshot.Slot), Text(embedding.Fingerprint)], ct).ConfigureAwait(false);
            foreach (var id in rows)
            {
                await ExecuteAsync(
                    connection, transaction, InsertBitsSql,
                    [Uuid(id), Text(embedding.Model), Text(embedding.Namespace), Bits(embedding.Bits)], ct).ConfigureAwait(false);
                if (embedding.Q8 is { } q8)
                {
                    await ExecuteAsync(connection, transaction, InsertVectorSql, [Uuid(id), Text(embedding.Model), Bytes(q8)], ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>The surviving history of a slot as steps in the order they happened: episodes (each followed by its changed-source retirement), retractions, restorations.</summary>
    private static List<Step> Timeline(SlotSnapshot snapshot)
    {
        var steps = new List<(DateTimeOffset At, int Phase, Guid Tie, Step Step)>();
        var subjectOf = snapshot.AllRows.ToDictionary(r => r.Id);
        var surviving = new HashSet<string>(StringComparer.Ordinal);

        // One ingest of an assertion is one (episode, assertion, locator, quote); the same line is copied onto every piece of a row.
        var ingests = new List<(EpisodeInfo Episode, SlotSource Source, SlotRow Row)>();
        var seen = new HashSet<(Guid, string, string, string?)>();
        foreach (var source in snapshot.AllSources)
        {
            if (source.Assertion.Length == 0 || snapshot.Purged.Contains(source.EpisodeId) || !seen.Add((source.EpisodeId, source.Assertion, source.Locator, source.Quote)))
            {
                continue;
            }

            ingests.Add((snapshot.Episodes[source.EpisodeId], source, subjectOf[source.FactId]));
            surviving.Add(source.Assertion);
        }

        foreach (var byEpisode in ingests.GroupBy(i => i.Episode.Id))
        {
            var episode = byEpisode.First().Episode;
            foreach (var item in byEpisode.OrderBy(i => i.Source.From).ThenBy(i => i.Source.Assertion, StringComparer.Ordinal).ThenBy(i => i.Source.Locator, StringComparer.Ordinal))
            {
                var fact = new NewFact(
                    item.Row.Subject, item.Row.Predicate, item.Source.Value, item.Source.From!.Value, item.Source.To, item.Source.Text,
                    item.Source.Locator, item.Source.Quote, item.Source.Entity);
                steps.Add((episode.RecordedAt, 0, episode.Id, new IngestStep(episode, fact)));
            }
        }

        // An episode with earlier versions retires what only those support, whether or not it wrote to this slot.
        var retiring = ingests.Select(i => i.Episode).Concat(snapshot.Episodes.Values.Where(e => !snapshot.Purged.Contains(e.Id))).Where(e => e.HasEarlier).DistinctBy(e => e.Id);
        foreach (var episode in retiring)
        {
            steps.Add((episode.RecordedAt, 0, episode.Id, new ChangedStep(episode)));
        }

        foreach (var block in snapshot.AllRetractions.Where(b => surviving.Contains(b.Fingerprint)))
        {
            steps.Add((block.RetractedAt, 1, Guid.Empty, new RetractStep(block.Fingerprint, block.RetractedAt)));
            if (block.RestoredAt is { } restored)
            {
                steps.Add((restored, 2, Guid.Empty, new RestoreStep(block.Fingerprint, restored)));
            }
        }

        // Ingest steps sort before the same episode's changed-source step (their type order), so repeated facts gain the new source first.
        return [.. steps.OrderBy(s => s.At).ThenBy(s => s.Phase).ThenBy(s => s.Tie).ThenBy(s => s.Step is ChangedStep ? 1 : 0).Select(s => s.Step)];
    }

    private static async Task LockSlotsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] slots, CancellationToken ct)
    {
        if (slots.Length == 0)
        {
            return;
        }

        var batch = new NpgsqlBatch(connection, transaction);
        await using var batchScope = batch.ConfigureAwait(false);
        foreach (var slot in slots.Order(StringComparer.Ordinal))
        {
            Add(batch, LockSlotSql, Text(slot));
        }

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, NpgsqlParameter[] parameters, CancellationToken ct)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, NpgsqlParameter[] parameters, CancellationToken ct)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Guid[]> ReadGuidsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, NpgsqlParameter[] parameters, CancellationToken ct)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddRange(parameters);
        var ids = new List<Guid>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }

    private static async Task<string[]> ReadStringsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, NpgsqlParameter[] parameters, CancellationToken ct)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddRange(parameters);
        var values = new List<string>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private sealed record SlotRow(
        Guid Id, string Fingerprint, Guid Subject, string Predicate, string? Value, Guid? ObjectId, string Text, DateTimeOffset Lower, DateTimeOffset? Upper,
        DateTimeOffset RecordedAt, DateTimeOffset? RetiredAt, string? Reason, Guid EpisodeId, bool Superseded);

    private sealed record SlotSource(
        Guid FactId, Guid EpisodeId, string Locator, string? Quote, string Assertion, DateTimeOffset? From, DateTimeOffset? To, string? Value, Guid? Entity, string? Text);

    private sealed record SlotRetraction(string Fingerprint, DateTimeOffset RetractedAt, DateTimeOffset? RestoredAt);

    private sealed record SlotEmbedding(string Fingerprint, string Model, string Namespace, BitArray Bits, byte[]? Q8);

    private sealed record EpisodeInfo(Guid Id, DateTimeOffset RecordedAt, int Trust, string SourceKind, string SourceRef, bool HasEarlier);

    private sealed class SlotSnapshot(
        string slot, List<SlotRow> rows, List<SlotSource> sources, List<SlotRetraction> retractions, List<SlotEmbedding> embeddings,
        Dictionary<Guid, EpisodeInfo> episodes, HashSet<Guid> purged)
    {
        public string Slot { get; } = slot;

        public List<SlotRow> AllRows { get; } = rows;

        public List<SlotSource> AllSources { get; } = sources;

        public List<SlotRetraction> AllRetractions { get; } = retractions;

        public List<SlotEmbedding> Embeddings { get; } = embeddings;

        public Dictionary<Guid, EpisodeInfo> Episodes { get; } = episodes;

        public HashSet<Guid> Purged { get; } = purged;
    }

    private abstract record Step;

    private sealed record IngestStep(EpisodeInfo Episode, NewFact Fact) : Step;

    private sealed record ChangedStep(EpisodeInfo Episode) : Step;

    private sealed record RetractStep(string Fingerprint, DateTimeOffset At) : Step;

    private sealed record RestoreStep(string Fingerprint, DateTimeOffset At) : Step;
}
