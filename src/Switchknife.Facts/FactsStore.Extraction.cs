using Npgsql;
using Switchknife.Facts.Extraction;

namespace Switchknife.Facts;

/// <summary>Where an episode stands.</summary>
public enum EpisodeStatus
{
    /// <summary>Registered but not finished. The library does not write this today.</summary>
    Pending,

    /// <summary>Stored with its facts.</summary>
    Done,

    /// <summary>An extraction failed; no facts were written, and a retry with the same hash replaces this record.</summary>
    Failed,
}

public sealed partial class FactsStore
{
    private const string EpisodeStatusSql =
        "select status from facts.episodes where namespace = $1 and source_kind = $2 and source_ref = $3 and content_hash = $4";

    private const string DeleteFailedSql =
        "delete from facts.episodes where namespace = $1 and source_kind = $2 and source_ref = $3 and content_hash = $4 and status = 'failed'";

    private const string RecordFailureSql =
        """
        insert into facts.episodes (id, namespace, source_kind, source_ref, content_hash, occurred_at, recorded_at, trust, status, extractor, model, input_tokens, output_tokens, failure)
        values ($1, $2, $3, $4, $5, $6, $7, $8, 'failed', $9, $10, $11, $12, $13)
        on conflict (namespace, source_kind, source_ref, content_hash) do update set
            recorded_at = excluded.recorded_at, extractor = excluded.extractor, model = excluded.model,
            input_tokens = excluded.input_tokens, output_tokens = excluded.output_tokens, failure = excluded.failure
        where facts.episodes.status = 'failed'
        returning 1
        """;

    // The old row ($4) is retired in favour of the live row of the new assertion ($3), unless it is no longer live, is not a many/statement
    // row of the new fact's subject ($5), is the new row itself, or has a source of higher trust than the episode ($6).
    private const string ReplaceSql =
        """
        update facts.facts old set retired_at = $1, retired_reason = 'superseded', superseded_by = nw.id
        from (select id from facts.facts where namespace = $2 and fingerprint = $3 and slot is null and retired_at is null) nw
        where old.id = $4 and old.namespace = $2 and old.slot is null and old.retired_at is null and old.subject_id = $5 and old.id <> nw.id
          and coalesce((select max(case e.trust when 'owner' then 2 when 'agent' then 1 else 0 end)
                        from facts.fact_sources s join facts.episodes e on e.id = s.episode_id where s.fact_id = old.id), 0) <= $6
        """;

    private const string EntitiesByNameSql =
        """
        select id from facts.entities
        where namespace = $1 and merged_into is null and lower(btrim(kind)) = lower(btrim($2)) and lower(btrim(name)) = lower(btrim($3)) limit 2
        """;

    /// <summary>Whether an episode of this source and hash is stored, and how it ended; null when none is. The pipeline asks before it pays for a model call.</summary>
    public async Task<EpisodeStatus?> EpisodeStatusAsync(string sourceKind, string sourceRef, string contentHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        var command = _data.CreateCommand(EpisodeStatusSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(sourceKind));
        command.Parameters.Add(Text(sourceRef));
        command.Parameters.Add(Text(contentHash));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            "done" => EpisodeStatus.Done,
            "failed" => EpisodeStatus.Failed,
            "pending" => EpisodeStatus.Pending,
            _ => null,
        };
    }

    /// <summary>
    /// Records that extracting an episode failed: a <c>failed</c> episode with a short code (lower-case letters, digits and underscores,
    /// at most 40, so no source text can be stored in it), the extractor if known, and no facts. A retry with the same hash replaces it.
    /// Never touches an episode that is already done.
    /// </summary>
    /// <returns>False when the episode is already done and nothing was written.</returns>
    public async Task<bool> RecordFailureAsync(NewEpisode episode, ExtractorInfo? extractor, string code, CancellationToken cancellationToken = default)
    {
        Validate(episode, []);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 40 || !char.IsAsciiLetterLower(code[0]) || !code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new ArgumentException("A failure code is 1 to 40 characters: a lower-case letter, then lower-case letters, digits and underscores.", nameof(code));
        }

        var now = Micro(_time.GetUtcNow());
        var command = _data.CreateCommand(RecordFailureSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Uuid(Guid.CreateVersion7(now)));
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(episode.SourceKind));
        command.Parameters.Add(Text(episode.SourceRef));
        command.Parameters.Add(Text(episode.ContentHash));
        command.Parameters.Add(Time(Micro(episode.OccurredAt ?? now)));
        command.Parameters.Add(Time(now));
        command.Parameters.Add(Text(ToText(episode.Trust)));
        command.Parameters.Add(Text(extractor?.Name));
        command.Parameters.Add(Text(extractor?.Model));
        command.Parameters.Add(Int(extractor?.InputTokens));
        command.Parameters.Add(Int(extractor?.OutputTokens));
        command.Parameters.Add(Text(code));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Writes validated extractor output in one transaction of its own: the entities are resolved first (through <paramref name="resolver"/>,
    /// on its own connection), then the episode, with the extractor and model, and its facts go through the same write path as
    /// <see cref="IngestAsync(NewEpisode, IReadOnlyList{NewFact}, CancellationToken)"/>, then each accepted <c>replaces</c> is applied.
    /// A failed episode of the same hash is replaced.
    /// </summary>
    public async Task<ExtractedIngestResult> IngestExtractedAsync(
        NewEpisode episode, ValidatedExtraction validated, ExtractorInfo extractor, IEntityResolver resolver, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareExtractedAsync(episode, validated, extractor, resolver, cancellationToken).ConfigureAwait(false);
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var result = await WriteExtractedAsync(connection, transaction, episode, prepared, extractor, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The same inside the caller's transaction. The resolver still runs on its own connection, so an entity it creates stays when the caller rolls back.</summary>
    public async Task<ExtractedIngestResult> IngestExtractedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewEpisode episode, ValidatedExtraction validated, ExtractorInfo extractor, IEntityResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var prepared = await PrepareExtractedAsync(episode, validated, extractor, resolver, cancellationToken).ConfigureAwait(false);
        return await WriteExtractedAsync(connection, transaction, episode, prepared, extractor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pipeline for one episode: if it is already stored, stop; otherwise ask <paramref name="extractor"/> (holding no connection),
    /// read and check the reply against <paramref name="context"/>, and write it. A failed call or an unreadable reply is recorded as a
    /// failed episode and reported in the outcome; it writes no facts and does not throw. Cancellation propagates and records nothing.
    /// </summary>
    /// <param name="extractor">The consumer's model call.</param>
    /// <param name="episode">The source and its hash.</param>
    /// <param name="context">What the extractor is shown and the output is checked against.</param>
    /// <param name="resolver">Places entity names; <see cref="ExactNameEntityResolver"/> by default.</param>
    /// <param name="options">Validation limits and prompt rules.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<ExtractionOutcome> ExtractAsync(
        IFactExtractor extractor, NewEpisode episode, ExtractionContext context, IEntityResolver? resolver = null, ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(context);
        Validate(episode, []);
        if (await EpisodeStatusAsync(episode.SourceKind, episode.SourceRef, episode.ContentHash, cancellationToken).ConfigureAwait(false) is EpisodeStatus.Done)
        {
            return new ExtractionOutcome(ExtractionStatus.Duplicate, null, null, null, null);
        }

        options ??= new ExtractionOptions();
        var prompt = ExtractionPrompt.Build(context, options);
        ExtractorResponse response;
        try
        {
            response = await extractor.ExtractAsync(new ExtractionRequest(prompt, context), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(episode, new ExtractorInfo(prompt.ExtractorName, null), ExtractionFailures.ExtractorError, error, cancellationToken).ConfigureAwait(false);
        }

        var info = (response.Info ?? new ExtractorInfo(null, null)) with { Name = response.Info?.Name ?? prompt.ExtractorName };
        if (!ExtractionParser.TryParse(response.Text, out var output, out var failure))
        {
            return await FailAsync(
                episode, info, failure == ParseFailure.BadJson ? ExtractionFailures.BadJson : ExtractionFailures.BadShape, null, cancellationToken).ConfigureAwait(false);
        }

        var validated = ExtractionValidator.Validate(output, context, options);
        var ingest = await IngestExtractedAsync(episode, validated, info, resolver ?? new ExactNameEntityResolver(this), cancellationToken).ConfigureAwait(false);
        return new ExtractionOutcome(ingest.Ingest.Duplicate ? ExtractionStatus.Duplicate : ExtractionStatus.Ingested, ingest, validated, null, null);
    }

    /// <summary>The entity of this kind and name, created if there is none; null when two or more already share the name.</summary>
    internal async Task<Guid?> ResolveByExactNameAsync(string kind, string name, CancellationToken ct)
    {
        var connection = await _data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        // One lock per name, so two episodes mentioning a new name at once make one entity, not two.
        await LockSlotsAsync(connection, transaction, [string.Create(System.Globalization.CultureInfo.InvariantCulture, $"entity:{_namespace}:{kind.Trim().ToLowerInvariant()}:{name.Trim().ToLowerInvariant()}")], ct).ConfigureAwait(false);
        var found = new List<Guid>();
        var select = new NpgsqlCommand(EntitiesByNameSql, connection, transaction);
        await using (select.ConfigureAwait(false))
        {
            select.Parameters.Add(Text(_namespace));
            select.Parameters.Add(Text(kind));
            select.Parameters.Add(Text(name));
            var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                found.Add(reader.GetGuid(0));
            }
        }

        if (found.Count > 1)
        {
            return null;
        }

        if (found.Count == 1)
        {
            return found[0];
        }

        var now = Micro(_time.GetUtcNow());
        var insert = new NpgsqlCommand(EntitySql, connection, transaction);
        await using (insert.ConfigureAwait(false))
        {
            insert.Parameters.Add(Uuid(Guid.CreateVersion7(now)));
            insert.Parameters.Add(Text(_namespace));
            insert.Parameters.Add(Text(kind.Trim()));
            insert.Parameters.Add(Text(name.Trim()));
            insert.Parameters.Add(Text(null));
            insert.Parameters.Add(Time(now));
            var id = (Guid)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return id;
        }
    }

    private async Task<ExtractionOutcome> FailAsync(NewEpisode episode, ExtractorInfo info, string code, Exception? error, CancellationToken ct)
    {
        await RecordFailureAsync(episode, info, code, ct).ConfigureAwait(false);
        return new ExtractionOutcome(ExtractionStatus.Failed, null, null, code, error);
    }

    private async Task<Prepared> PrepareExtractedAsync(
        NewEpisode episode, ValidatedExtraction validated, ExtractorInfo extractor, IEntityResolver resolver, CancellationToken ct)
    {
        Validate(episode, []);
        ArgumentNullException.ThrowIfNull(validated);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(resolver);
        if (validated.Facts.Any(f => f.Predicate == ExtractionValidator.StatementPredicate))
        {
            await DefinePredicateAsync(ExtractionValidator.StatementPredicate, Cardinality.Many, ct).ConfigureAwait(false);
        }

        var placed = new Dictionary<(string, string), Guid?>();
        async Task<Guid?> PlaceAsync(EntityMention mention)
        {
            if (mention.KnownId is { } known)
            {
                return known;
            }

            var key = (mention.Kind.Trim().ToLowerInvariant(), mention.Name.Trim().ToLowerInvariant());
            if (!placed.TryGetValue(key, out var id))
            {
                id = await resolver.ResolveAsync(mention, ct).ConfigureAwait(false);
                placed[key] = id;
            }

            return id;
        }

        var facts = new List<NewFact>();
        var replaces = new List<(NewFact Fact, Guid Old)>();
        var unresolved = 0;
        foreach (var fact in validated.Facts)
        {
            var subject = await PlaceAsync(fact.Subject).ConfigureAwait(false);
            Guid? target = fact.ObjectEntity is { } objectMention ? await PlaceAsync(objectMention).ConfigureAwait(false) : null;
            if (subject is not { } subjectId || fact.ObjectEntity is not null && target is null)
            {
                unresolved++;
                continue;
            }

            var created = new NewFact(subjectId, fact.Predicate, target is null ? fact.Value : null, fact.ValidFrom, fact.ValidTo, fact.Text, fact.LocatorJson, fact.Quote, target);
            facts.Add(created);
            if (fact.Replaces is { } old)
            {
                replaces.Add((created, old));
            }
        }

        return new Prepared(facts, replaces, unresolved);
    }

    private async Task<ExtractedIngestResult> WriteExtractedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewEpisode episode, Prepared prepared, ExtractorInfo extractor, CancellationToken ct)
    {
        Validate(episode, prepared.Facts);
        var written = await WriteAsync(connection, transaction, episode, prepared.Facts, extractor, true, ct).ConfigureAwait(false);
        int replaced = 0, skipped = 0;
        if (!written.Duplicate)
        {
            var now = Micro(_time.GetUtcNow());
            foreach (var (fact, old) in prepared.Replaces)
            {
                var command = new NpgsqlCommand(ReplaceSql, connection, transaction);
                await using var commandScope = command.ConfigureAwait(false);
                command.Parameters.Add(Time(now));
                command.Parameters.Add(Text(_namespace));
                command.Parameters.Add(Text(Fingerprint(fact, null)));
                command.Parameters.Add(Uuid(old));
                command.Parameters.Add(Uuid(fact.Subject));
                command.Parameters.Add(Int((int)episode.Trust));
                if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                {
                    replaced++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new ExtractedIngestResult(written, replaced, skipped, prepared.Unresolved);
    }

    private async Task DeleteFailedEpisodeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewEpisode episode, CancellationToken ct)
    {
        var command = new NpgsqlCommand(DeleteFailedSql, connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(episode.SourceKind));
        command.Parameters.Add(Text(episode.SourceRef));
        command.Parameters.Add(Text(episode.ContentHash));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record Prepared(List<NewFact> Facts, List<(NewFact Fact, Guid Old)> Replaces, int Unresolved);
}
