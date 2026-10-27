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
public sealed class FactsStore
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
        insert into facts.episodes (id, namespace, source_kind, source_ref, content_hash, occurred_at, recorded_at, status)
        values ($1, $2, $3, $4, $5, $6, $7, 'done')
        on conflict (namespace, source_kind, source_ref, content_hash) do nothing
        returning id
        """;

    private const string EpisodeIdSql =
        "select id from facts.episodes where namespace = $1 and source_kind = $2 and source_ref = $3 and content_hash = $4";

    private const string LockSql = "select pg_advisory_xact_lock(hashtextextended($1, 0))";

    private const string LiveSlotSql =
        "select id, lower(valid), upper(valid), object_value from facts.facts where slot = $1 and retired_at is null order by lower(valid)";

    private const string InsertFactSql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, slot, fingerprint, episode_id)
        values ($1, $2, $3, $4, $5, $6, tstzrange($7, $8, $9), $10, $11, $12, $13)
        on conflict (namespace, fingerprint) where retired_at is null do nothing
        """;

    private const string InsertSourceByFingerprintSql =
        """
        insert into facts.fact_sources (fact_id, episode_id, locator, quote)
        select id, $1, $2, $3 from facts.facts where namespace = $4 and fingerprint = $5 and retired_at is null
        on conflict do nothing
        """;

    private const string RetireSql =
        "update facts.facts set retired_at = $2, retired_reason = 'superseded' where id = $1";

    private const string NarrowSql =
        """
        insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, slot, fingerprint, confidence, episode_id)
        select $1, namespace, subject_id, predicate, object_value, text, tstzrange(lower(valid), $3, '[)'), $4, slot, fingerprint, confidence, episode_id
        from facts.facts where id = $2
        """;

    private const string CopySourcesSql =
        "insert into facts.fact_sources (fact_id, episode_id, locator, quote) select $1, episode_id, locator, quote from facts.fact_sources where fact_id = $2";

    private const string AddSourceSql =
        "insert into facts.fact_sources (fact_id, episode_id, locator, quote) values ($1, $2, $3, $4) on conflict do nothing";

    private const string SupersededBySql = "update facts.facts set superseded_by = $2 where id = $1";

    private const string AsOfColumns =
        "select id, subject_id, predicate, object_value, text, lower(valid), upper(valid), recorded_at from facts.facts";

    private const string AsOfWhere =
        " where namespace = $1 and subject_id = $2 and valid @> $3 and recorded_at <= $4 and (retired_at is null or retired_at > $4)";

    private const string HistorySql =
        """
        select id, subject_id, predicate, object_value, text, lower(valid), upper(valid), recorded_at, retired_at, retired_reason, superseded_by
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
            versions.Add(new FactVersion(
                ReadFact(reader),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10)));
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
        foreach (var fact in facts)
        {
            ArgumentNullException.ThrowIfNull(fact);
            ArgumentException.ThrowIfNullOrWhiteSpace(fact.Predicate);
            ArgumentException.ThrowIfNullOrWhiteSpace(fact.Value);
            ArgumentException.ThrowIfNullOrWhiteSpace(fact.LocatorJson);
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
        // Every predicate is known and every ValidTo allowed before the first write, so a bad fact leaves nothing half-written.
        var cardinalities = new Cardinality[facts.Count];
        for (var i = 0; i < facts.Count; i++)
        {
            cardinalities[i] = await CardinalityAsync(connection, transaction, facts[i].Predicate, ct).ConfigureAwait(false);
            if (facts[i].ValidTo is not null && cardinalities[i] != Cardinality.Many)
            {
                throw new ArgumentException($"ValidTo is only supported for 'many' predicates; '{facts[i].Predicate}' is {cardinalities[i]}.", nameof(facts));
            }
        }

        // recorded_at is this clock, never the episode's own date: a query "as known at K" cannot see what arrived after K.
        var now = Micro(_time.GetUtcNow());
        var episodeId = Guid.CreateVersion7(now);
        var insert = new NpgsqlCommand(EpisodeSql, connection, transaction);
        await using (insert.ConfigureAwait(false))
        {
            insert.Parameters.Add(Uuid(episodeId));
            insert.Parameters.Add(Text(_namespace));
            insert.Parameters.Add(Text(episode.SourceKind));
            insert.Parameters.Add(Text(episode.SourceRef));
            insert.Parameters.Add(Text(episode.ContentHash));
            insert.Parameters.Add(Time(Micro(episode.OccurredAt ?? now)));
            insert.Parameters.Add(Time(now));
            if (await insert.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                var existing = new NpgsqlCommand(EpisodeIdSql, connection, transaction);
                await using var existingScope = existing.ConfigureAwait(false);
                existing.Parameters.Add(Text(_namespace));
                existing.Parameters.Add(Text(episode.SourceKind));
                existing.Parameters.Add(Text(episode.SourceRef));
                existing.Parameters.Add(Text(episode.ContentHash));
                return new IngestResult((Guid)(await existing.ExecuteScalarAsync(ct).ConfigureAwait(false))!, Duplicate: true, Written: 0, Corroborated: 0);
            }
        }

        // Slots are locked in one order whatever the order of the facts, so two episodes never wait on each other in a cycle.
        var slots = facts.Where((f, i) => cardinalities[i] == Cardinality.One).Select(Slot).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (slots.Length > 0)
        {
            var locks = new NpgsqlBatch(connection, transaction);
            await using var locksScope = locks.ConfigureAwait(false);
            foreach (var slot in slots)
            {
                Add(locks, LockSql, Text(slot));
            }

            await locks.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int written = 0, corroborated = 0;
        for (var i = 0; i < facts.Count; i++)
        {
            var wrote = cardinalities[i] == Cardinality.One
                ? await WriteOneAsync(connection, transaction, episodeId, now, facts[i], ct).ConfigureAwait(false)
                : await WriteIndependentAsync(connection, transaction, episodeId, now, facts[i], cardinalities[i], ct).ConfigureAwait(false);
            if (wrote)
            {
                written++;
            }
            else
            {
                corroborated++;
            }
        }

        return new IngestResult(episodeId, Duplicate: false, written, corroborated);
    }

    /// <summary>A <see cref="Cardinality.Many"/> or <see cref="Cardinality.Event"/> fact: inserted unless the same assertion is live, in which case it gains a source.</summary>
    private async Task<bool> WriteIndependentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid episodeId, DateTimeOffset now, NewFact fact, Cardinality cardinality, CancellationToken ct)
    {
        var from = Micro(fact.ValidFrom);
        var isEvent = cardinality == Cardinality.Event;
        var fingerprint = Fingerprint(fact, isEvent ? from : null);
        var batch = new NpgsqlBatch(connection, transaction);
        await using var batchScope = batch.ConfigureAwait(false);
        Add(
            batch,
            InsertFactSql,
            Uuid(Guid.CreateVersion7(now)), Text(_namespace), Uuid(fact.Subject), Text(fact.Predicate), Text(fact.Value), Text(RenderText(fact)),
            Time(from), Time(isEvent ? from : fact.ValidTo is { } to ? Micro(to) : null), Text(isEvent ? "[]" : "[)"),
            Time(now), Text(null), Text(fingerprint), Uuid(episodeId));
        Add(batch, InsertSourceByFingerprintSql, Uuid(episodeId), Json(fact.LocatorJson), Text(fact.Quote), Text(_namespace), Text(fingerprint));
        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return batch.BatchCommands[0].RecordsAffected == 1;
    }

    /// <summary>
    /// A <see cref="Cardinality.One"/> fact. The live row it lands in is retired and written again ending where the new value
    /// begins; the new value ends where the next known one begins, or is open. The same value inside a live row only adds a source.
    /// </summary>
    private async Task<bool> WriteOneAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid episodeId, DateTimeOffset now, NewFact fact, CancellationToken ct)
    {
        var from = Micro(fact.ValidFrom);
        var slot = Slot(fact);
        var live = new List<LiveRow>();
        var select = new NpgsqlCommand(LiveSlotSql, connection, transaction);
        await using (select.ConfigureAwait(false))
        {
            select.Parameters.Add(Text(slot));
            var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                live.Add(new LiveRow(
                    reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3)));
            }
        }

        var plan = Plan(live, from, fact.Value);
        var batch = new NpgsqlBatch(connection, transaction);
        await using var batchScope = batch.ConfigureAwait(false);
        if (plan.SameValue is { } same)
        {
            Add(batch, AddSourceSql, Uuid(same), Uuid(episodeId), Json(fact.LocatorJson), Text(fact.Quote));
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return false;
        }

        var created = Guid.CreateVersion7(now);
        if (plan.Containing is { } containing)
        {
            Add(batch, RetireSql, Uuid(containing.Id), Time(now));
            if (containing.Lower < from)
            {
                var narrowed = Guid.CreateVersion7(now);
                Add(batch, NarrowSql, Uuid(narrowed), Uuid(containing.Id), Time(from), Time(now));
                Add(batch, CopySourcesSql, Uuid(narrowed), Uuid(containing.Id));
            }
        }

        Add(
            batch,
            InsertFactSql,
            Uuid(created), Text(_namespace), Uuid(fact.Subject), Text(fact.Predicate), Text(fact.Value), Text(RenderText(fact)),
            Time(from), Time(plan.Upper), Text("[)"), Time(now), Text(slot), Text(Fingerprint(fact, from)), Uuid(episodeId));
        Add(batch, AddSourceSql, Uuid(created), Uuid(episodeId), Json(fact.LocatorJson), Text(fact.Quote));
        if (plan.Containing is { } retired)
        {
            Add(batch, SupersededBySql, Uuid(retired.Id), Uuid(created));
        }

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Decides what a value beginning at <paramref name="from"/> does to the live rows of its slot. Pure, so the rules read in one place.</summary>
    private static SlotPlan Plan(IReadOnlyList<LiveRow> live, DateTimeOffset from, string value)
    {
        var containing = live.FirstOrDefault(r => r.Lower <= from && (r.Upper is null || from < r.Upper));
        if (containing is not null)
        {
            return Normalize(containing.Value) == Normalize(value) ? new SlotPlan(containing, containing.Id, null) : new SlotPlan(containing, null, containing.Upper);
        }

        var next = live.Where(r => r.Lower > from).Select(r => (DateTimeOffset?)r.Lower).Min();
        return new SlotPlan(null, null, next);
    }

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

    private static Fact ReadFact(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7));

    private static string Slot(NewFact fact) => string.Create(CultureInfo.InvariantCulture, $"{fact.Subject}/{fact.Predicate}");

    private static string RenderText(NewFact fact) => fact.Text ?? string.Create(CultureInfo.InvariantCulture, $"{fact.Predicate.Replace('_', ' ')} {fact.Value}");

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string Fingerprint(NewFact fact, DateTimeOffset? from)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{fact.Subject}|{fact.Predicate}|{Normalize(fact.Value)}|{from?.UtcTicks}");
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

    private static NpgsqlParameter Json(string value) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = value };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value is { } v ? v.ToUniversalTime() : DBNull.Value };

    private sealed record LiveRow(Guid Id, DateTimeOffset Lower, DateTimeOffset? Upper, string Value);

    /// <summary>The outcome for one value of a <see cref="Cardinality.One"/> slot: the live row it lands in (to retire), the live row that already says the same (to add a source to), and where the new row ends.</summary>
    private sealed record SlotPlan(LiveRow? Containing, Guid? SameValue, DateTimeOffset? Upper);
}
