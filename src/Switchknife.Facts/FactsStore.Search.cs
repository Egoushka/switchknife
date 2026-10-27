using Npgsql;

namespace Switchknife.Facts;

/// <summary>A fact found by <see cref="FactsStore.SearchAsync"/> and why it ranked where it did.</summary>
/// <param name="Fact">The fact.</param>
/// <param name="Score">The sum of <c>1 / (60 + rank)</c> over the lists that contain it.</param>
/// <param name="TextRank">Its place (1 is best) in the full-text list, or null when that list does not contain it.</param>
/// <param name="VectorRank">Its place in the vector list, or null when that list does not contain it.</param>
/// <param name="Similarity">Its cosine similarity to the query vector, or null when the vector list does not contain it.</param>
public sealed record SearchHit(Fact Fact, double Score, int? TextRank, int? VectorRank, double? Similarity);

public sealed partial class FactsStore
{
    private const int RankConstant = 60;
    private const int CandidateDepth = 4;
    private const int MaxSearchLimit = 1000;

    private const string TextListSql =
        """
        select id, subject_id, predicate, object_value, text, lower(valid), upper(valid), recorded_at, object_id from facts.facts
        where namespace = $1 and search @@ plainto_tsquery('simple', $2) and valid @> $3 and recorded_at <= $4 and (retired_at is null or retired_at > $4)
        order by ts_rank_cd(search, plainto_tsquery('simple', $2)) desc, id limit $5
        """;

    /// <summary>
    /// Facts matching a text, a vector or both, best first. Each given input makes a ranked list of <paramref name="limit"/> times four
    /// candidates, believed at <paramref name="knownAt"/> to be true at <paramref name="validAt"/>: full text
    /// (<c>plainto_tsquery('simple', …)</c>, ordered by <c>ts_rank_cd</c>) and <see cref="NearestAsync"/>. The lists run at the same
    /// time and are fused by reciprocal rank: a fact scores the sum of <c>1 / (60 + rank)</c> over the lists that hold it, so one in
    /// both lists beats one that leads a single list. Ties go to the better rank, then the smaller id. A hit says which lists held it.
    /// </summary>
    /// <param name="text">Words to match, or null or blank for no text list.</param>
    /// <param name="vector">A vector of the model's length, or empty for no vector list.</param>
    /// <param name="model">The embedding model of <paramref name="vector"/>; required with a vector.</param>
    /// <param name="limit">How many facts to return, 1 to 1,000.</param>
    /// <param name="validAt">The instant in the world; defaults to now.</param>
    /// <param name="knownAt">The instant of belief; defaults to now. A future instant is refused.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <exception cref="ArgumentException">Neither text nor vector is given, a vector comes without a model, or the vector is not finite or is zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is outside 1 to 1,000, or <paramref name="knownAt"/> is after the store's clock.</exception>
    /// <exception cref="PostgresException">The vector's length differs from the stored vectors' (SQLSTATE 22026).</exception>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string? text, ReadOnlyMemory<float> vector, string? model, int limit = 10, DateTimeOffset? validAt = null, DateTimeOffset? knownAt = null,
        CancellationToken cancellationToken = default)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasVector = !vector.IsEmpty;
        if (!hasText && !hasVector)
        {
            throw new ArgumentException("Give a text, a vector or both.", nameof(text));
        }

        if (hasVector)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxSearchLimit);
        var now = Micro(_time.GetUtcNow());
        var known = knownAt is { } at ? Micro(at) : now;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(known, now, nameof(knownAt));
        var valid = Micro(validAt ?? now);

        var depth = limit * CandidateDepth;
        var textList = hasText ? TextListAsync(text!, valid, known, depth, cancellationToken) : Task.FromResult<IReadOnlyList<Fact>>([]);
        var vectorList = hasVector ? NearestAsync(model!, vector, depth, valid, known, cancellationToken: cancellationToken) : Task.FromResult<IReadOnlyList<ScoredFact>>([]);
        await Task.WhenAll(textList, vectorList).ConfigureAwait(false);
        return Fuse(textList.Result, vectorList.Result, limit);
    }

    private static List<SearchHit> Fuse(IReadOnlyList<Fact> text, IReadOnlyList<ScoredFact> vector, int limit)
    {
        var found = new Dictionary<Guid, (Fact Fact, int? Text, int? Vector, double? Similarity)>();
        for (var i = 0; i < text.Count; i++)
        {
            found[text[i].Id] = (text[i], i + 1, null, null);
        }

        for (var i = 0; i < vector.Count; i++)
        {
            var hit = vector[i];
            found[hit.Fact.Id] = found.TryGetValue(hit.Fact.Id, out var both) ? (both.Fact, both.Text, i + 1, hit.Similarity) : (hit.Fact, null, i + 1, hit.Similarity);
        }

        var hits = found.Values
            .Select(f => new SearchHit(f.Fact, Reciprocal(f.Text) + Reciprocal(f.Vector), f.Text, f.Vector, f.Similarity))
            .ToList();
        hits.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byRank = Best(a).CompareTo(Best(b));
            return byRank != 0 ? byRank : string.CompareOrdinal(a.Fact.Id.ToString(), b.Fact.Id.ToString());
        });
        return hits.Count > limit ? hits.GetRange(0, limit) : hits;

        static double Reciprocal(int? rank) => rank is { } r ? 1.0 / (RankConstant + r) : 0;
        static int Best(SearchHit hit) => Math.Min(hit.TextRank ?? int.MaxValue, hit.VectorRank ?? int.MaxValue);
    }

    private async Task<IReadOnlyList<Fact>> TextListAsync(string text, DateTimeOffset valid, DateTimeOffset known, int depth, CancellationToken ct)
    {
        var command = _data.CreateCommand(TextListSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(Text(_namespace));
        command.Parameters.Add(Text(text));
        command.Parameters.Add(Time(valid));
        command.Parameters.Add(Time(known));
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = depth });
        var facts = new List<Fact>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            facts.Add(ReadFact(reader));
        }

        return facts;
    }
}
