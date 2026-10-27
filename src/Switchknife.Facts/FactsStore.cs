using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Switchknife.Facts;

/// <summary>
/// Writes and reads facts in one namespace of the <c>facts</c> schema (<see cref="FactsSchema"/>). Rows are immutable:
/// a later value for a <see cref="Cardinality.One"/> predicate retires the row it lands in and writes it again ending
/// where the new value begins, so both "what was true at T" and "what did we believe at K" stay answerable.
/// </summary>
/// <remarks>
/// Writers of one slot (subject and predicate) are serialized by a transaction-scoped advisory lock; the exclusion
/// constraint in the schema stays the backstop for writers that bypass this class. The store does not own the data source.
/// </remarks>
public sealed partial class FactsStore
{
    private const string DefineSql =
        """
        insert into facts.predicates (namespace, name, cardinality) values ($1, $2, $3)
        on conflict (namespace, name) do update set cardinality = facts.predicates.cardinality
        returning cardinality
        """;

    private const string CardinalitySql = "select cardinality from facts.predicates where namespace = $1 and name = $2";

    private const string EntitySql =
        """
        insert into facts.entities (id, namespace, kind, name, external_ref, created_at) values ($1, $2, $3, $4, $5, $6)
        on conflict (namespace, external_ref) do update set name = facts.entities.name
        returning id
        """;

    private const string EpisodeSql =
        """
        insert into facts.episodes (id, namespace, source_kind, source_ref, content_hash, occurred_at, recorded_at, trust, status)
        values ($1, $2, $3, $4, $5, $6, $7, $8, 'done')
        on conflict (namespace, source_kind, source_ref, content_hash) do nothing
        returning (select count(*) from facts.episodes where namespace = $2 and source_kind = $3 and source_ref = $4 and (recorded_at, id) < ($7, $1))
        """;

    private const string EpisodeIdSql =
        "select id from facts.episodes where namespace = $1 and source_kind = $2 and source_ref = $3 and content_hash = $4";

    // Locks the slots of live facts the source's earlier versions support (the changed-source retirement retires them), sorted so
    // two ingests never wait on each other in a cycle. The lock function is evaluated after the sort, which is what the planner
    // does with a volatile select-list function. Runs only when the source has earlier versions.
    private const string LockStaleSlotsSql =
        """
        select pg_advisory_xact_lock(hashtextextended(slot, 0)) from (
            select distinct f.slot from facts.facts f
            where f.namespace = $1 and f.retired_at is null and f.slot is not null
              and f.id in (
                  select x.fact_id from facts.fact_sources x join facts.episodes e on e.id = x.episode_id
                  where e.namespace = $1 and e.source_kind = $2 and e.source_ref = $3 and (e.recorded_at, e.id) < ($5, $4))
        ) as slots(slot) order by slot
        """;

    private const string LockSlotSql = "select pg_advisory_xact_lock(hashtextextended($1, 0))";

    // The live rows of a slot. The second statement of the batch says whether the fingerprint was retracted, in the same round trip.
    private const string LiveSlotSql =
        """
        select id, lower(valid), upper(valid), object_value, object_id, 0
        from facts.facts where slot = $1 and retired_at is null order by lower(valid)
        """;

    // The same with the highest trust among each row's sources. Only an episode below owner trust can be outranked, so only it pays for this.
    private const string LiveSlotTrustSql =
        """
        select f.id, lower(f.valid), upper(f.valid), f.object_value, f.object_id,
            coalesce((select max(case e.trust when 'owner' then 2 when 'agent' then 1 else 0 end)
                      from facts.fact_sources s join facts.episodes e on e.id = s.episode_id where s.fact_id = f.id), 0)
        from facts.facts f where f.slot = $1 and f.retired_at is null order by lower(f.valid)
        """;

    private const string RetractedSql = "select exists (select 1 from facts.retractions where namespace = $1 and fingerprint = $2 and restored_at is null)";

    private const string InsertManySql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, object_id, text, valid, recorded_at, slot, fingerprint, episode_id)
        select $1, $2, $3, $4, $5, $6, $7, tstzrange($8, $9, $10), $11, $12, $13, $14
        where not exists (select 1 from facts.retractions where namespace = $2 and fingerprint = $13 and restored_at is null)
        on conflict (namespace, fingerprint) where retired_at is null and slot is null do nothing
        """;

    private const string InsertOneSql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, object_id, text, valid, recorded_at, slot, fingerprint, episode_id)
        values ($1, $2, $3, $4, $5, $6, $7, tstzrange($8, $9, $10), $11, $12, $13, $14)
        """;

    private const string InsertOutrankedSql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, object_id, text, valid, recorded_at, slot, fingerprint, episode_id, retired_at, retired_reason)
        values ($1, $2, $3, $4, $5, $6, $7, tstzrange($8, $9, $10), $11, $12, $13, $14, $11, 'outranked')
        """;

    private const string InsertSourceByFingerprintSql =
        """
        insert into facts.fact_sources (fact_id, episode_id, locator, quote)
        select id, $1, $2, $3 from facts.facts where namespace = $4 and fingerprint = $5 and retired_at is null and slot is null
        on conflict do nothing
        """;

    private const string RetireSql =
        "update facts.facts set retired_at = $2, retired_reason = 'superseded' where id = $1";

    // The part of a clipped row that stays: before the new value begins ($3 is the new upper) ...
    private const string NarrowSql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, object_id, text, valid, recorded_at, slot, fingerprint, confidence, episode_id)
        select $1, namespace, subject_id, predicate, object_value, object_id, text, tstzrange(lower(valid), $3, '[)'), $4, slot, fingerprint, confidence, episode_id
        from facts.facts where id = $2
        """;

    // ... and after a closed range ends ($3 is the new lower).
    private const string TailSql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, object_id, text, valid, recorded_at, slot, fingerprint, confidence, episode_id)
        select $1, namespace, subject_id, predicate, object_value, object_id, text, tstzrange($3, upper(valid), '[)'), $4, slot, fingerprint, confidence, episode_id
        from facts.facts where id = $2
        """;

    private const string CopySourcesSql =
        """
        insert into facts.fact_sources (fact_id, episode_id, locator, quote, assertion, assertion_from, assertion_to, assertion_value, assertion_entity, assertion_text)
        select $1, episode_id, locator, quote, assertion, assertion_from, assertion_to, assertion_value, assertion_entity, assertion_text
        from facts.fact_sources where fact_id = $2
        """;

    private const string AddSourceSql =
        """
        insert into facts.fact_sources (fact_id, episode_id, locator, quote, assertion, assertion_from, assertion_to, assertion_value, assertion_entity, assertion_text)
        values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) on conflict do nothing
        """;

    private const string SupersededBySql = "update facts.facts set superseded_by = $2 where id = $1";

    // Live facts whose every source is an earlier version of the ingested source ($5 is the new episode) are no longer supported.
    private const string RetireChangedSql =
        """
        update facts.facts f set retired_at = $1, retired_reason = 'source_changed'
        where f.namespace = $2 and f.retired_at is null
          and f.id in (
              select x.fact_id from facts.fact_sources x join facts.episodes e on e.id = x.episode_id
              where e.namespace = $2 and e.source_kind = $3 and e.source_ref = $4 and (e.recorded_at, e.id) < ($1, $5))
          and not exists (
              select 1 from facts.fact_sources x join facts.episodes e on e.id = x.episode_id
              where x.fact_id = f.id and not (e.namespace = $2 and e.source_kind = $3 and e.source_ref = $4 and (e.recorded_at, e.id) < ($1, $5)))
        """;

    private const string AsOfColumns =
        "select id, subject_id, predicate, object_value, text, lower(valid), upper(valid), recorded_at, object_id from facts.facts";

    private const string AsOfWhere =
        " where namespace = $1 and subject_id = $2 and valid @> $3 and recorded_at <= $4 and (retired_at is null or retired_at > $4)";

    private const string HistorySql =
        """
        select id, subject_id, predicate, object_value, text, lower(valid), upper(valid), recorded_at, object_id, retired_at, retired_reason, superseded_by
        from facts.facts where namespace = $1 and subject_id = $2 and predicate = $3 order by recorded_at, lower(valid), id
        """;

    private readonly NpgsqlDataSource _data;
    private readonly string _namespace;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Cardinality> _predicates = new(StringComparer.Ordinal);

    /// <summary>Creates a store over <paramref name="data"/>; nothing touches the database until a method is called.</summary>
    /// <param name="data">A data source on a database the <see cref="FactsSchema"/> was applied to.</param>
    /// <param name="namespace">The namespace every row of this store carries (<c>nytka</c>, <c>feed-eater</c>).</param>
    /// <param name="time">The ingest clock: <c>recorded_at</c> and the default "known at" come from it. Defaults to the system clock.</param>
    public FactsStore(NpgsqlDataSource data, string @namespace, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        _data = data;
        _namespace = @namespace;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Defines a predicate, or confirms it is already defined with the same cardinality. A predicate's cardinality
    /// never changes: rows written under it depend on it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The predicate exists with another cardinality.</exception>
    public async Task DefinePredicateAsync(string name, Cardinality cardinality, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var command = _data.CreateCommand(DefineSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(name));
        command.Parameters.Add(Text(ToText(cardinality)));
        var stored = ParseCardinality((string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        if (stored != cardinality)
        {
            throw new InvalidOperationException($"Predicate '{name}' is already defined with cardinality {stored}; it cannot become {cardinality}.");
        }

        _predicates[name] = stored;
    }

    /// <summary>
    /// Creates an entity and returns its id. With an <paramref name="externalRef"/> (the consumer's own id for the thing)
    /// the same reference always returns the same entity and never makes a second one.
    /// </summary>
    public async Task<Guid> EnsureEntityAsync(string kind, string name, string? externalRef = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = Micro(_time.GetUtcNow());
        var command = _data.CreateCommand(EntitySql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Uuid(Guid.CreateVersion7(now)));
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(kind));
        command.Parameters.Add(Text(name));
        command.Parameters.Add(Text(externalRef));
        command.Parameters.Add(Time(now));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Writes an episode and its facts in one transaction of its own.</summary>
    public async Task<IngestResult> IngestAsync(NewEpisode episode, IReadOnlyList<NewFact> facts, CancellationToken cancellationToken = default)
    {
        Validate(episode, facts);
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var result = await WriteAsync(connection, transaction, episode, facts, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Writes an episode and its facts inside the caller's transaction, so they commit or roll back with the caller's own
    /// changes. A thrown exception leaves the transaction usable only if it was not a database error; roll it back.
    /// </summary>
    public Task<IngestResult> IngestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewEpisode episode, IReadOnlyList<NewFact> facts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        Validate(episode, facts);
        return WriteAsync(connection, transaction, episode, facts, cancellationToken);
    }

    /// <summary>
    /// What the store believed at <paramref name="knownAt"/> was true at <paramref name="validAt"/> about
    /// <paramref name="subject"/>: rows recorded by then and not yet retired then. Normally one row per predicate.
    /// </summary>
    /// <param name="subject">The entity.</param>
    /// <param name="predicate">One predicate, or null for all of them.</param>
    /// <param name="validAt">The instant in the world.</param>
    /// <param name="knownAt">The instant of belief; defaults to now (the store's clock). A future instant is refused: nothing is known yet.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="knownAt"/> is after the store's clock.</exception>
    public async Task<IReadOnlyList<Fact>> AsOfAsync(
        Guid subject, string? predicate, DateTimeOffset validAt, DateTimeOffset? knownAt = null, CancellationToken cancellationToken = default)
    {
        var now = Micro(_time.GetUtcNow());
        var known = knownAt is { } k ? Micro(k) : now;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(known, now, nameof(knownAt));

        var command = _data.CreateCommand(predicate is null
            ? AsOfColumns + AsOfWhere + " order by predicate, lower(valid)"
            : AsOfColumns + AsOfWhere + " and predicate = $5 order by lower(valid)");
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Uuid(subject));
        command.Parameters.Add(Time(Micro(validAt)));
        command.Parameters.Add(Time(known));
        if (predicate is not null)
        {
            command.Parameters.Add(Text(predicate));
        }

        var facts = new List<Fact>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            facts.Add(ReadFact(reader));
        }

        return facts;
    }

    /// <summary>Every row ever written for a slot, live and retired, oldest first, with both timelines.</summary>
    public async Task<IReadOnlyList<FactVersion>> HistoryAsync(Guid subject, string predicate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        var command = _data.CreateCommand(HistorySql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Uuid(subject));
        command.Parameters.Add(Text(predicate));
        var versions = new List<FactVersion>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    private static void Validate(NewEpisode episode, IReadOnlyList<NewFact> facts)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(episode.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(episode.SourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(episode.ContentHash);
        if (!Enum.IsDefined(episode.Trust))
        {
            throw new ArgumentOutOfRangeException(nameof(episode), episode.Trust, "Unknown trust.");
        }

        foreach (var fact in facts)
        {
            ArgumentNullException.ThrowIfNull(fact);
            ArgumentException.ThrowIfNullOrWhiteSpace(fact.Predicate);
            ArgumentException.ThrowIfNullOrWhiteSpace(fact.LocatorJson);
            if (fact.ObjectEntity is null == string.IsNullOrWhiteSpace(fact.Value))
            {
                throw new ArgumentException($"A fact about '{fact.Predicate}' needs exactly one of Value and ObjectEntity.", nameof(facts));
            }

            if (fact.ObjectEntity is not null && string.IsNullOrWhiteSpace(fact.Text))
            {
                throw new ArgumentException($"A fact about '{fact.Predicate}' points at an entity, so its Text is required.", nameof(facts));
            }

            if (RenderText(fact).Length > 300)
            {
                throw new ArgumentException($"The text of a fact about '{fact.Predicate}' is longer than 300 characters.", nameof(facts));
            }

            if (fact.ValidTo is { } to && Micro(to) <= Micro(fact.ValidFrom))
            {
                throw new ArgumentException($"A fact about '{fact.Predicate}' ends ({to:O}) before it begins ({fact.ValidFrom:O}).", nameof(facts));
            }
        }
    }

    private async Task<IngestResult> WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewEpisode episode, IReadOnlyList<NewFact> facts, CancellationToken ct)
    {
        // Every predicate is known before the first write, so a bad fact leaves nothing half-written.
        var cardinalities = new Cardinality[facts.Count];
        for (var i = 0; i < facts.Count; i++)
        {
            cardinalities[i] = await CardinalityAsync(connection, transaction, facts[i].Predicate, ct).ConfigureAwait(false);
        }

        // The slots are locked before the clock is read, so writers of one slot are recorded in the order they are applied; a purge
        // or a verification replays the slot in recorded order and so reproduces it.
        var slots = facts.Where((f, i) => cardinalities[i] == Cardinality.One).Select(Slot).Distinct(StringComparer.Ordinal).ToArray();
        await LockSlotsAsync(connection, transaction, slots, ct).ConfigureAwait(false);

        // recorded_at is this clock, never the episode's own date: a query "as known at K" cannot see what arrived after K.
        var now = Micro(_time.GetUtcNow());
        var episodeId = Guid.CreateVersion7(now);
        var earlierVersions = await OpenEpisodeAsync(connection, transaction, episode, episodeId, now, ct).ConfigureAwait(false);
        if (earlierVersions is not { } stale)
        {
            var existing = new NpgsqlCommand(EpisodeIdSql, connection, transaction);
            await using var existingScope = existing.ConfigureAwait(false);
            existing.Parameters.Add(Text(_namespace));
            existing.Parameters.Add(Text(episode.SourceKind));
            existing.Parameters.Add(Text(episode.SourceRef));
            existing.Parameters.Add(Text(episode.ContentHash));
            return new IngestResult((Guid)(await existing.ExecuteScalarAsync(ct).ConfigureAwait(false))!, Duplicate: true, Written: 0, Corroborated: 0);
        }

        if (stale > 0)
        {
            var lockStale = new NpgsqlCommand(LockStaleSlotsSql, connection, transaction);
            await using (lockStale.ConfigureAwait(false))
            {
                lockStale.Parameters.Add(Text(_namespace));
                lockStale.Parameters.Add(Text(episode.SourceKind));
                lockStale.Parameters.Add(Text(episode.SourceRef));
                lockStale.Parameters.Add(Uuid(episodeId));
                lockStale.Parameters.Add(Time(now));
                await lockStale.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        int written = 0, corroborated = 0, outranked = 0;
        var trust = (int)episode.Trust;
        for (var i = 0; i < facts.Count; i++)
        {
            var outcome = cardinalities[i] == Cardinality.One
                ? await WriteOneAsync(connection, transaction, episodeId, trust, now, facts[i], ct).ConfigureAwait(false)
                : await WriteIndependentAsync(connection, transaction, episodeId, now, facts[i], cardinalities[i], ct).ConfigureAwait(false);
            switch (outcome)
            {
                case WriteOutcome.Written:
                    written++;
                    break;
                case WriteOutcome.Corroborated:
                    corroborated++;
                    break;
                case WriteOutcome.Outranked:
                    outranked++;
                    break;
            }
        }

        if (stale > 0)
        {
            // After the new facts, so a fact the new version repeats has already gained the new episode as a source.
            await RetireChangedAsync(connection, transaction, episode.SourceKind, episode.SourceRef, episodeId, now, ct).ConfigureAwait(false);
        }

        return new IngestResult(episodeId, Duplicate: false, written, corroborated, outranked);
    }

    /// <summary>Retires, at <paramref name="now"/>, the live facts whose every source is an earlier version of the source of <paramref name="episodeId"/>.</summary>
    private async Task RetireChangedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sourceKind, string sourceRef, Guid episodeId, DateTimeOffset now, CancellationToken ct)
    {
        var retire = new NpgsqlCommand(RetireChangedSql, connection, transaction);
        await using var retireScope = retire.ConfigureAwait(false);
        retire.Parameters.Add(Time(now));
        retire.Parameters.Add(Text(_namespace));
        retire.Parameters.Add(Text(sourceKind));
        retire.Parameters.Add(Text(sourceRef));
        retire.Parameters.Add(Uuid(episodeId));
        await retire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Inserts the episode. Null when it (same source and hash) already exists; otherwise how many other episodes of the same source came before it.</summary>
    private async Task<long?> OpenEpisodeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewEpisode episode, Guid episodeId, DateTimeOffset now, CancellationToken ct)
    {
        var command = new NpgsqlCommand(EpisodeSql, connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Uuid(episodeId));
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(episode.SourceKind));
        command.Parameters.Add(Text(episode.SourceRef));
        command.Parameters.Add(Text(episode.ContentHash));
        command.Parameters.Add(Time(Micro(episode.OccurredAt ?? now)));
        command.Parameters.Add(Time(now));
        command.Parameters.Add(Text(ToText(episode.Trust)));
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as long?;
    }

    /// <summary>A <see cref="Cardinality.Many"/> or <see cref="Cardinality.Event"/> fact: inserted unless the same assertion is live (it gains a source) or was retracted (it is skipped).</summary>
    private async Task<WriteOutcome> WriteIndependentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid episodeId, DateTimeOffset now, NewFact fact, Cardinality cardinality, CancellationToken ct)
    {
        var from = Micro(fact.ValidFrom);
        var isEvent = cardinality == Cardinality.Event;
        var fingerprint = Fingerprint(fact, isEvent ? from : null);
        var batch = new NpgsqlBatch(connection, transaction);
        await using var batchScope = batch.ConfigureAwait(false);
        Add(
            batch,
            InsertManySql,
            Uuid(Guid.CreateVersion7(now)), Text(_namespace), Uuid(fact.Subject), Text(fact.Predicate), Text(fact.Value), Uuid(fact.ObjectEntity), Text(RenderText(fact)),
            Time(from), Time(isEvent ? from : Micro(fact.ValidTo)), Text(isEvent ? "[]" : "[)"),
            Time(now), Text(null), Text(fingerprint), Uuid(episodeId));
        Add(batch, InsertSourceByFingerprintSql, Uuid(episodeId), Json(fact.LocatorJson), Text(fact.Quote), Text(_namespace), Text(fingerprint));
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (batch.BatchCommands[0].RecordsAffected == 1)
        {
            return WriteOutcome.Written;
        }

        // Nothing inserted: the assertion is live (and gained a source), or it was retracted (and gained nothing).
        return batch.BatchCommands[1].RecordsAffected == 1 ? WriteOutcome.Corroborated : WriteOutcome.Skipped;
    }

    /// <summary>
    /// A <see cref="Cardinality.One"/> fact. The live row it lands in is retired and written again ending where the new value
    /// begins; the new value ends at its own end, where the row it landed in ended, or where the next known value begins, whichever
    /// is first, and a closed range hands the rest of that row back to the old value. The same value inside a live row only adds a
    /// source. A row of higher trust is never retired: the new value is stored already retired instead.
    /// </summary>
    private async Task<WriteOutcome> WriteOneAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid episodeId, int trust, DateTimeOffset now, NewFact fact, CancellationToken ct)
    {
        var from = Micro(fact.ValidFrom);
        var to = Micro(fact.ValidTo);
        var slot = Slot(fact);
        var key = ObjectKey(fact);
        var fingerprint = Fingerprint(fact, from);
        var live = new List<LiveRow>();
        bool retracted;
        var read = new NpgsqlBatch(connection, transaction);
        await using (read.ConfigureAwait(false))
        {
            Add(read, trust < (int)EpisodeTrust.Owner ? LiveSlotTrustSql : LiveSlotSql, Text(slot));
            Add(read, RetractedSql, Text(_namespace), Text(fingerprint));
            var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                live.Add(new LiveRow(
                    reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? ObjectKey(reader.GetGuid(4)) : Normalize(reader.GetString(3)), reader.GetInt32(5)));
            }

            await reader.NextResultAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            retracted = reader.GetBoolean(0);
        }

        if (retracted)
        {
            return WriteOutcome.Skipped;
        }

        var plan = Plan(live, from, key);
        var batch = new NpgsqlBatch(connection, transaction);
        await using var batchScope = batch.ConfigureAwait(false);
        if (plan.SameValue is { } same)
        {
            Add(batch, AddSourceSql, SourceParameters(same, episodeId, fact, from, fingerprint));
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return WriteOutcome.Corroborated;
        }

        var created = Guid.CreateVersion7(now);
        var end = Earliest(to, plan.Upper);
        if (plan.Containing is { } higher && higher.Trust > trust)
        {
            Add(batch, InsertOutrankedSql, InsertFactParameters(created, fact, from, end, now, slot, fingerprint, episodeId));
            Add(batch, AddSourceSql, SourceParameters(created, episodeId, fact, from, fingerprint));
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return WriteOutcome.Outranked;
        }

        if (plan.Containing is { } containing)
        {
            Add(batch, RetireSql, Uuid(containing.Id), Time(now));
            if (containing.Lower < from)
            {
                var narrowed = Guid.CreateVersion7(now);
                Add(batch, NarrowSql, Uuid(narrowed), Uuid(containing.Id), Time(from), Time(now));
                Add(batch, CopySourcesSql, Uuid(narrowed), Uuid(containing.Id));
            }

            if (to is { } closed && (containing.Upper is null || closed < containing.Upper))
            {
                var tail = Guid.CreateVersion7(now);
                Add(batch, TailSql, Uuid(tail), Uuid(containing.Id), Time(closed), Time(now));
                Add(batch, CopySourcesSql, Uuid(tail), Uuid(containing.Id));
            }
        }

        Add(batch, InsertOneSql, InsertFactParameters(created, fact, from, end, now, slot, fingerprint, episodeId));
        Add(batch, AddSourceSql, SourceParameters(created, episodeId, fact, from, fingerprint));
        if (plan.Containing is { } retired)
        {
            Add(batch, SupersededBySql, Uuid(retired.Id), Uuid(created));
        }

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return WriteOutcome.Written;
    }

    private NpgsqlParameter[] InsertFactParameters(
        Guid id, NewFact fact, DateTimeOffset from, DateTimeOffset? to, DateTimeOffset now, string? slot, string fingerprint, Guid episodeId) =>
    [
        Uuid(id), Text(_namespace), Uuid(fact.Subject), Text(fact.Predicate), Text(fact.Value), Uuid(fact.ObjectEntity), Text(RenderText(fact)),
        Time(from), Time(to), Text("[)"), Time(now), Text(slot), Text(fingerprint), Uuid(episodeId),
    ];

    /// <summary>A source line of a single-valued fact: where it was read, and what the episode asserted, which purge replays.</summary>
    private static NpgsqlParameter[] SourceParameters(Guid factId, Guid episodeId, NewFact fact, DateTimeOffset from, string fingerprint) =>
    [
        Uuid(factId), Uuid(episodeId), Json(fact.LocatorJson), Text(fact.Quote), Text(fingerprint), Time(from), Time(Micro(fact.ValidTo)),
        Text(fact.Value), Uuid(fact.ObjectEntity), Text(RenderText(fact)),
    ];

    /// <summary>Decides what a value beginning at <paramref name="from"/> does to the live rows of its slot. Pure, so the rules read in one place.</summary>
    private static SlotPlan Plan(IReadOnlyList<LiveRow> live, DateTimeOffset from, string key)
    {
        var containing = live.FirstOrDefault(r => r.Lower <= from && (r.Upper is null || from < r.Upper));
        if (containing is not null)
        {
            return containing.Key == key ? new SlotPlan(containing, containing.Id, null) : new SlotPlan(containing, null, containing.Upper);
        }

        var next = live.Where(r => r.Lower > from).Select(r => (DateTimeOffset?)r.Lower).Min();
        return new SlotPlan(null, null, next);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private async Task<Cardinality> CardinalityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string predicate, CancellationToken ct)
    {
        if (_predicates.TryGetValue(predicate, out var known))
        {
            return known;
        }

        var command = new NpgsqlCommand(CardinalitySql, connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(predicate));
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string stored)
        {
            throw new InvalidOperationException($"Predicate '{predicate}' is not defined in namespace '{_namespace}'; call DefinePredicateAsync first.");
        }

        return _predicates[predicate] = ParseCardinality(stored);
    }

    private static FactVersion ReadVersion(NpgsqlDataReader reader) => new(
        ReadFact(reader),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetGuid(11));

    private static Fact ReadFact(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8));

    private static string Slot(NewFact fact) => string.Create(CultureInfo.InvariantCulture, $"{fact.Subject}/{fact.Predicate}");

    private static string RenderText(NewFact fact) => fact.Text ?? string.Create(CultureInfo.InvariantCulture, $"{fact.Predicate.Replace('_', ' ')} {fact.Value}");

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>What two values are compared by: the normalised text, or the entity's id behind a control character no text value starts with.</summary>
    private static string ObjectKey(NewFact fact) => fact.ObjectEntity is { } entity ? ObjectKey(entity) : Normalize(fact.Value!);

    private static string ObjectKey(Guid entity) => string.Create(CultureInfo.InvariantCulture, $"\u0001{entity:N}");

    private static string Fingerprint(NewFact fact, DateTimeOffset? from)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{fact.Subject}|{fact.Predicate}|{ObjectKey(fact)}|{from?.UtcTicks}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>PostgreSQL keeps microseconds; comparing at finer resolution would call a stored instant different from the one written.</summary>
    private static DateTimeOffset Micro(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private static DateTimeOffset? Micro(DateTimeOffset? value) => value is { } v ? Micro(v) : null;

    private static string ToText(Cardinality cardinality) => cardinality switch
    {
        Cardinality.One => "one",
        Cardinality.Many => "many",
        Cardinality.Event => "event",
        _ => throw new ArgumentOutOfRangeException(nameof(cardinality), cardinality, "Unknown cardinality."),
    };

    private static string ToText(EpisodeTrust trust) => trust switch
    {
        EpisodeTrust.Owner => "owner",
        EpisodeTrust.Agent => "agent",
        _ => "third_party",
    };

    private static Cardinality ParseCardinality(string text) => text switch
    {
        "one" => Cardinality.One,
        "many" => Cardinality.Many,
        "event" => Cardinality.Event,
        _ => throw new InvalidOperationException($"Unknown cardinality '{text}' in facts.predicates."),
    };

    private static void Add(NpgsqlBatch batch, string sql, params NpgsqlParameter[] parameters)
    {
        var command = batch.CreateBatchCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        batch.BatchCommands.Add(command);
    }

    private static NpgsqlParameter Text(string? value) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Uuid(Guid value) => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value };

    private static NpgsqlParameter Uuid(Guid? value) => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value is { } v ? v : DBNull.Value };

    private static NpgsqlParameter Json(string value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value is { } v ? v.ToUniversalTime() : DBNull.Value };

    private sealed record LiveRow(Guid Id, DateTimeOffset Lower, DateTimeOffset? Upper, string Key, int Trust);

    private enum WriteOutcome
    {
        Written,
        Corroborated,
        Outranked,
        Skipped,
    }

    /// <summary>The outcome for one value of a <see cref="Cardinality.One"/> slot: the live row it lands in (to retire), the live row that already says the same (to add a source to), and where the new row ends.</summary>
    private sealed record SlotPlan(LiveRow? Containing, Guid? SameValue, DateTimeOffset? Upper);
}
