using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Switchknife.Facts.Bench;

/// <summary>
/// The design's write and read paths, written as SQL against <c>Schema/0001_facts.sql</c>. This is the reference the
/// library will be built from, kept small so the benchmark measures the design and a rewrite can be compared with it.
/// </summary>
internal sealed class FactsStore(NpgsqlDataSource data)
{
    public const string Namespace = "bench";

    /// <summary>Applies the embedded schema script. With <paramref name="fresh"/> the <c>facts</c> schema is replaced, so only point this at a database made for the benchmark.</summary>
    public async Task CreateSchemaAsync(bool fresh)
    {
        if (fresh)
        {
            await ExecuteAsync("drop schema if exists facts cascade").ConfigureAwait(false);
        }

        var assembly = typeof(FactsStore).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await ExecuteAsync(await reader.ReadToEndAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    public async Task SeedAsync(Workload workload, DateTime now)
    {
        await using var connection = await data.OpenConnectionAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var predicate in Workload.SinglePredicates.Append(Workload.ManyPredicate))
        {
            var cardinality = predicate == Workload.ManyPredicate ? "many" : "one";
            await Run(connection, transaction, "insert into facts.predicates values ($1, $2, $3)", Namespace, predicate, cardinality).ConfigureAwait(false);
        }

        var index = 0;
        foreach (var chunk in workload.Subjects.Chunk(500))
        {
            await using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var subject in chunk)
            {
                var command = new NpgsqlBatchCommand("insert into facts.entities (id, namespace, kind, name, created_at) values ($1, $2, 'person', $3, $4)");
                command.Parameters.Add(P(subject));
                command.Parameters.Add(P(Namespace));
                command.Parameters.Add(P(string.Create(CultureInfo.InvariantCulture, $"person {index++}")));
                command.Parameters.Add(P(now));
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>One episode: its facts and their effect on what they supersede, in one transaction.</summary>
    public async Task IngestAsync(IReadOnlyList<Learned> singles, IReadOnlyList<Added> many, int episodeNumber)
    {
        var recorded = singles.Select(s => s.RecordedAt).Concat(many.Select(m => m.RecordedAt)).Max();
        await using var connection = await data.OpenConnectionAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        var episode = Guid.NewGuid();
        var reference = string.Create(CultureInfo.InvariantCulture, $"episode-{episodeNumber}");
        await Run(connection, transaction,
            "insert into facts.episodes (id, namespace, source_kind, source_ref, content_hash, occurred_at, recorded_at, status) values ($1, $2, 'bench', $3, $3, $4, $4, 'done')",
            episode, Namespace, reference, recorded).ConfigureAwait(false);

        foreach (var learned in singles)
        {
            await LearnAsync(connection, transaction, episode, learned).ConfigureAwait(false);
        }

        foreach (var added in many)
        {
            var id = Guid.NewGuid();
            await Run(connection, transaction,
                """
                insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, fingerprint, episode_id)
                values ($1, $2, $3, $4, $5, $6, tstzrange($7, null, '[)'), $8, $9, $10)
                """,
                id, Namespace, added.Subject, Workload.ManyPredicate, added.Value,
                string.Create(CultureInfo.InvariantCulture, $"{Workload.ManyPredicate} {added.Value}"), added.ValidFrom, added.RecordedAt,
                Fingerprint(added.Subject, Workload.ManyPredicate, added.Value, added.ValidFrom), episode).ConfigureAwait(false);
            await AddSourceAsync(connection, transaction, id, episode).ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A new value of a single-valued predicate. The row it lands in is retired and written again ending where the new
    /// value begins, so "what did we believe at K" stays answerable; the new value ends where the next known one begins.
    /// </summary>
    private static async Task LearnAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid episode, Learned learned)
    {
        var slot = string.Create(CultureInfo.InvariantCulture, $"{learned.Subject}/{learned.Predicate}");
        var live = new List<LiveRow>();
        await using (var select = new NpgsqlCommand(
            "select id, lower(valid), upper(valid) from facts.facts where slot = $1 and retired_at is null order by lower(valid) for update",
            connection, transaction))
        {
            select.Parameters.Add(P(slot));
            await using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                live.Add(new LiveRow(reader.GetGuid(0), reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
            }
        }

        if (live.Any(r => r.Lower == learned.ValidFrom))
        {
            return;
        }

        var containing = live.FirstOrDefault(r => r.Lower < learned.ValidFrom && (r.Upper is null || learned.ValidFrom < r.Upper));
        DateTime? upper = containing is not null ? containing.Upper : live.Where(r => r.Lower > learned.ValidFrom).Select(r => (DateTime?)r.Lower).Min();
        var created = Guid.NewGuid();

        if (containing is not null)
        {
            var narrowed = Guid.NewGuid();
            await Run(connection, transaction, "update facts.facts set retired_at = $2, retired_reason = 'superseded' where id = $1", containing.Id, learned.RecordedAt).ConfigureAwait(false);
            await Run(connection, transaction,
                """
                insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, slot, fingerprint, episode_id)
                select $1, namespace, subject_id, predicate, object_value, text, tstzrange(lower(valid), $3, '[)'), $4, slot, fingerprint, episode_id
                from facts.facts where id = $2
                """,
                narrowed, containing.Id, learned.ValidFrom, learned.RecordedAt).ConfigureAwait(false);
            await Run(connection, transaction,
                "insert into facts.fact_sources (fact_id, episode_id, locator, quote) select $1, episode_id, locator, quote from facts.fact_sources where fact_id = $2",
                narrowed, containing.Id).ConfigureAwait(false);
        }

        await Run(connection, transaction,
            """
            insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, slot, fingerprint, episode_id)
            values ($1, $2, $3, $4, $5, $6, tstzrange($7, $8, '[)'), $9, $10, $11, $12)
            """,
            created, Namespace, learned.Subject, learned.Predicate, learned.Value,
            string.Create(CultureInfo.InvariantCulture, $"{learned.Predicate.Replace("_", " ", StringComparison.Ordinal)} {learned.Value}"),
            learned.ValidFrom, upper, learned.RecordedAt, slot, Fingerprint(learned.Subject, learned.Predicate, learned.Value, learned.ValidFrom), episode).ConfigureAwait(false);
        await AddSourceAsync(connection, transaction, created, episode).ConfigureAwait(false);

        if (containing is not null)
        {
            await Run(connection, transaction, "update facts.facts set superseded_by = $2 where id = $1", containing.Id, created).ConfigureAwait(false);
        }
    }

    /// <summary>The values valid at <paramref name="validAt"/> among what was recorded by <paramref name="knownAt"/>: none, one, or (a violation) several.</summary>
    public async Task<IReadOnlyList<string>> AsOfAsync(Guid subject, string predicate, DateTime validAt, DateTime knownAt)
    {
        await using var command = data.CreateCommand(
            """
            select object_value from facts.facts
            where namespace = $1 and subject_id = $2 and predicate = $3
              and valid @> $4::timestamptz and recorded_at <= $5 and (retired_at is null or retired_at > $5)
            """);
        foreach (var value in new object[] { Namespace, subject, predicate, validAt, knownAt })
        {
            command.Parameters.Add(P(value));
        }

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public async Task<int> SearchAsync(string word)
    {
        await using var command = data.CreateCommand(
            "select count(*) from (select id from facts.facts where search @@ plainto_tsquery('simple', $1) and retired_at is null limit 10) hits");
        command.Parameters.Add(P(word));
        return (int)(long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    /// <summary>A random live fact's text word, so a search finds something.</summary>
    public async Task<IReadOnlyList<string>> SampleWordsAsync(int count)
    {
        await using var command = data.CreateCommand(
            "select split_part(text, ' ', array_length(string_to_array(text, ' '), 1)) from facts.facts where retired_at is null order by random() limit $1");
        command.Parameters.Add(P(count));
        var words = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            words.Add(reader.GetString(0));
        }

        return words;
    }

    public async Task<int> EmbedLiveAsync(int dimensions, int seed)
    {
        var random = new Random(seed);
        var ids = new List<Guid>();
        await using (var command = data.CreateCommand("select id from facts.facts where retired_at is null"))
        await using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        foreach (var chunk in ids.Chunk(500))
        {
            await using var connection = await data.OpenConnectionAsync().ConfigureAwait(false);
            await using var batch = new NpgsqlBatch(connection);
            foreach (var id in chunk)
            {
                var command = new NpgsqlBatchCommand("insert into facts.fact_embeddings values ($1, 'bench', $2::text::halfvec)");
                command.Parameters.Add(P(id));
                command.Parameters.Add(P(RandomVector(random, dimensions)));
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return ids.Count;
    }

    /// <summary>The ten live facts nearest a vector by an exact scan: no index, so recall is 1 by construction.</summary>
    public async Task<int> NearestAsync(string vector)
    {
        await using var command = data.CreateCommand(
            """
            select count(*) from (
              select e.fact_id from facts.fact_embeddings e join facts.facts f on f.id = e.fact_id
              where f.namespace = $1 and f.retired_at is null
              order by e.embedding <=> $2::text::halfvec limit 10) nearest
            """);
        command.Parameters.Add(P(Namespace));
        command.Parameters.Add(P(vector));
        return (int)(long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    public async Task<StoreSizes> SizesAsync()
    {
        await using var command = data.CreateCommand(
            """
            select pg_total_relation_size('facts.facts'), pg_total_relation_size('facts.fact_embeddings'),
                   (select count(*) from facts.facts where retired_at is null), (select count(*) from facts.facts where retired_at is not null),
                   current_setting('shared_buffers')
            """);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);
        return new StoreSizes(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
    }

    public static string RandomVector(Random random, int dimensions)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < dimensions; i++)
        {
            text.Append(i == 0 ? "" : ",").Append((random.NextSingle() - 0.5f).ToString("F4", CultureInfo.InvariantCulture));
        }

        return text.Append(']').ToString();
    }

    private static Task AddSourceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fact, Guid episode) =>
        Run(connection, transaction, "insert into facts.fact_sources (fact_id, episode_id, locator) values ($1, $2, '{}')", fact, episode);

    private static string Fingerprint(Guid subject, string predicate, string value, DateTime from) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{subject}|{predicate}|{value}|{from.Ticks}")))).ToLowerInvariant()[..32];

    private static NpgsqlParameter P(object? value) => new() { Value = value ?? DBNull.Value };

    private async Task ExecuteAsync(string sql)
    {
        await using var command = data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task Run(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object?[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(P(value));
        }

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed record LiveRow(Guid Id, DateTime Lower, DateTime? Upper);
}

internal sealed record StoreSizes(long FactsBytes, long EmbeddingBytes, long LiveRows, long RetiredRows, string SharedBuffers);
