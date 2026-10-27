using Npgsql;

namespace Switchknife.Facts.Bench;

/// <summary>
/// What the library does not offer yet, as plain SQL against its schema: full text, the live-fact list, sizes. Writing and
/// the as-of read go through <see cref="Switchknife.Facts.FactsStore"/>, so the numbers measure the code consumers run.
/// </summary>
internal sealed class Probes(NpgsqlDataSource data)
{
    public const string Namespace = "bench";

    /// <summary>Applies the embedded schema script. With <paramref name="fresh"/> the <c>facts</c> schema is replaced, so only point this at a database made for the benchmark.</summary>
    public async Task CreateSchemaAsync(bool fresh)
    {
        if (fresh)
        {
            await ExecuteAsync("drop schema if exists facts cascade").ConfigureAwait(false);
        }

        await ExecuteAsync(FactsSchema.Sql).ConfigureAwait(false);
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

    /// <summary>The live facts, and whether each is current (open-ended), which is what a search "valid far in the future" can return.</summary>
    public async Task<IReadOnlyList<(Guid Id, bool Current)>> LiveFactsAsync()
    {
        await using var command = data.CreateCommand("select id, upper(valid) is null from facts.facts where retired_at is null order by id");
        var rows = new List<(Guid, bool)>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add((reader.GetGuid(0), reader.GetBoolean(1)));
        }

        return rows;
    }

    public async Task<StoreSizes> SizesAsync()
    {
        await using var command = data.CreateCommand(
            """
            select pg_total_relation_size('facts.facts'), pg_total_relation_size('facts.fact_embeddings') + pg_total_relation_size('facts.fact_vectors'),
                   (select count(*) from facts.facts where retired_at is null), (select count(*) from facts.facts where retired_at is not null),
                   current_setting('shared_buffers')
            """);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);
        return new StoreSizes(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
    }

    private static NpgsqlParameter P(object? value) => new() { Value = value ?? DBNull.Value };

    private async Task ExecuteAsync(string sql)
    {
        await using var command = data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

internal sealed record StoreSizes(long FactsBytes, long EmbeddingBytes, long LiveRows, long RetiredRows, string SharedBuffers);
