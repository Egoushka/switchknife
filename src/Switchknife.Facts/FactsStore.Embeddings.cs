using System.Collections;
using Npgsql;
using NpgsqlTypes;

namespace Switchknife.Facts;

/// <summary>An embedding of a fact's text under a model, produced by the consumer.</summary>
/// <param name="FactId">The fact it belongs to (a row of this store's namespace).</param>
/// <param name="Vector">Any length, but one length per model. It is stored as sign bits and int8 of the unit vector.</param>
public sealed record FactEmbedding(Guid FactId, ReadOnlyMemory<float> Vector);

/// <summary>A fact and its cosine similarity to a query.</summary>
public sealed record ScoredFact(Fact Fact, double Similarity);

public sealed partial class FactsStore
{
    private const int EmbeddingBatch = 200;

    private const string UpsertBitsSql =
        """
        insert into facts.fact_embeddings (fact_id, model, namespace, bits)
        select id, $2, $3, $4 from facts.facts where id = $1 and namespace = $3
        on conflict (fact_id, model) do update set bits = excluded.bits
        """;

    private const string UpsertVectorSql =
        """
        insert into facts.fact_vectors (fact_id, model, q8)
        select id, $2, $3 from facts.facts where id = $1 and namespace = $4
        on conflict (fact_id, model) do update set q8 = excluded.q8
        """;

    private const string ScanSql =
        "select fact_id from facts.fact_embeddings where namespace = $1 and model = $2 order by bit_count(bits # $3) limit $4";

    // Two primary-key lookups by id array, not one join: with a join the planner may drive from the facts filter and probe the
    // vectors once per matching fact (measured: 21 ms for 400 candidates on a 1,000-fact table, growing with the table).
    private const string VisibleSql =
        """
        select id, subject_id, predicate, object_value, text, lower(valid), upper(valid), recorded_at from facts.facts
        where id = any($1) and namespace = $2 and valid @> $3 and recorded_at <= $4 and (retired_at is null or retired_at > $4)
        """;

    private const string VectorsSql = "select fact_id, q8 from facts.fact_vectors where model = $1 and fact_id = any($2)";

    /// <summary>
    /// Stores embeddings for facts of this namespace, replacing the ones already stored for the same fact and model. All or none:
    /// one transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">A fact is not a row of this namespace.</exception>
    /// <exception cref="ArgumentException">A vector is empty, not finite or zero.</exception>
    public async Task SetEmbeddingsAsync(string model, IReadOnlyList<FactEmbedding> embeddings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(embeddings);
        foreach (var embedding in embeddings)
        {
            ArgumentNullException.ThrowIfNull(embedding);
            Quantizer.Validate(embedding.Vector.Span, nameof(embeddings));
        }

        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        foreach (var chunk in embeddings.Chunk(EmbeddingBatch))
        {
            var batch = new NpgsqlBatch(connection, transaction);
            await using var batchScope = batch.ConfigureAwait(false);
            foreach (var embedding in chunk)
            {
                var unit = Quantizer.Unit(embedding.Vector.Span);
                Add(batch, UpsertBitsSql, Uuid(embedding.FactId), Text(model), Text(_namespace), Bits(Quantizer.Bits(unit)));
                Add(batch, UpsertVectorSql, Uuid(embedding.FactId), Text(model), Bytes(Quantizer.Int8(unit)), Text(_namespace));
            }

            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                if (batch.BatchCommands[2 * i].RecordsAffected != 1 || batch.BatchCommands[(2 * i) + 1].RecordsAffected != 1)
                {
                    throw new InvalidOperationException($"Fact {chunk[i].FactId} is not a row of namespace '{_namespace}'.");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <paramref name="k"/> facts most similar (cosine) to <paramref name="query"/> among those believed at
    /// <paramref name="knownAt"/> to be true at <paramref name="validAt"/>. A scan of the sign-bit codes proposes
    /// <paramref name="k"/> times <paramref name="oversample"/> candidates; the ones not visible at those instants are dropped (the
    /// scan widens fourfold when fewer than <paramref name="k"/> remain and more rows exist), and the survivors are ranked by cosine
    /// on their int8 vectors. The result is approximate: a true neighbour whose code ranks beyond the candidates is missed.
    /// </summary>
    /// <param name="model">The embedding model the vectors were stored under.</param>
    /// <param name="query">A vector of the model's length.</param>
    /// <param name="k">How many facts to return.</param>
    /// <param name="validAt">The instant in the world.</param>
    /// <param name="knownAt">The instant of belief; defaults to now. A future instant is refused.</param>
    /// <param name="oversample">Candidates per result, at least 1; higher costs time and raises recall. 40 returned results within 0.01 cosine of the exact top 10 in every benchmark run (docs/adr/0003-facts-vector-storage.md).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="PostgresException">The query's length differs from the stored vectors' (SQLSTATE 22026).</exception>
    public async Task<IReadOnlyList<ScoredFact>> NearestAsync(
        string model, ReadOnlyMemory<float> query, int k, DateTimeOffset validAt, DateTimeOffset? knownAt = null, int oversample = 40,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(oversample, 1);
        Quantizer.Validate(query.Span, nameof(query));
        var now = Micro(_time.GetUtcNow());
        var known = knownAt is { } at ? Micro(at) : now;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(known, now, nameof(knownAt));

        var unit = Quantizer.Unit(query.Span);
        var code = Quantizer.Bits(unit);
        for (long wanted = (long)k * oversample; ; wanted *= 4)
        {
            var limit = (int)Math.Min(wanted, int.MaxValue);
            var ids = await ScanAsync(model, code, limit, cancellationToken).ConfigureAwait(false);
            var ranked = await RankAsync(model, ids, unit, validAt, known, cancellationToken).ConfigureAwait(false);
            if (ranked.Count >= k || ids.Length < limit)
            {
                return [.. ranked.Take(k)];
            }
        }
    }

    private async Task<Guid[]> ScanAsync(string model, BitArray code, int limit, CancellationToken ct)
    {
        var command = _data.CreateCommand(ScanSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(model));
        command.Parameters.Add(Bits(code));
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = limit });
        var ids = new List<Guid>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return [.. ids];
    }

    private async Task<List<ScoredFact>> RankAsync(string model, Guid[] ids, float[] unitQuery, DateTimeOffset validAt, DateTimeOffset known, CancellationToken ct)
    {
        var visible = new List<Fact>();
        if (ids.Length > 0)
        {
            var command = _data.CreateCommand(VisibleSql);
            await using var commandScope = command.ConfigureAwait(false);
            command.Parameters.Add(Uuids(ids));
            command.Parameters.Add(Text(_namespace));
            command.Parameters.Add(Time(Micro(validAt)));
            command.Parameters.Add(Time(known));
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                visible.Add(ReadFact(reader));
            }
        }

        var ranked = new List<ScoredFact>(visible.Count);
        if (visible.Count > 0)
        {
            var vectors = _data.CreateCommand(VectorsSql);
            await using var vectorsScope = vectors.ConfigureAwait(false);
            vectors.Parameters.Add(Text(model));
            vectors.Parameters.Add(Uuids([.. visible.Select(f => f.Id)]));
            var byId = visible.ToDictionary(f => f.Id);
            var reader = await vectors.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ranked.Add(new ScoredFact(byId[reader.GetGuid(0)], Quantizer.Cosine(unitQuery, reader.GetFieldValue<byte[]>(1))));
            }
        }

        ranked.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return ranked;
    }

    private static NpgsqlParameter Uuids(Guid[] ids) => new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = ids };

    private static NpgsqlParameter Bits(BitArray bits) => new() { NpgsqlDbType = NpgsqlDbType.Varbit, Value = bits };

    private static NpgsqlParameter Bytes(byte[] bytes) => new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = bytes };
}
