using System.Globalization;
using System.Text;
using Npgsql;

namespace Switchknife.Facts.Bench;

/// <summary>
/// What the library does not offer yet, as plain SQL against its schema: full text, exact vector scan, sizes. Writing and
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

    private static NpgsqlParameter P(object? value) => new() { Value = value ?? DBNull.Value };

    private async Task ExecuteAsync(string sql)
    {
        await using var command = data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

}

internal sealed record StoreSizes(long FactsBytes, long EmbeddingBytes, long LiveRows, long RetiredRows, string SharedBuffers);
