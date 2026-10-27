using Npgsql;

namespace Switchknife.Facts.Tests;

public sealed class NearestTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Model = "test-model";

    private static float[] Random(Random random, int dimensions) =>
        [.. Enumerable.Range(0, dimensions).Select(_ => (float)(random.NextDouble() - 0.5))];

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / Math.Sqrt(na * nb);
    }

    /// <summary>Ingests <paramref name="count"/> <c>likes</c> facts at the clock and returns their ids in value order.</summary>
    private static async Task<(Guid Anna, Guid[] Ids)> LikesAsync(Harness db, string source, int count, int firstValue = 0)
    {
        var anna = await db.Store.EnsureEntityAsync("person", "Anna", "app:anna");
        var facts = Enumerable.Range(firstValue, count).Select(i => new NewFact(anna, "likes", $"thing {i}", Harness.Day(0))).ToArray();
        await db.IngestAsync(source, facts);
        var byValue = (await db.Store.AsOfAsync(anna, "likes", Harness.Day(1))).ToDictionary(f => f.Value, f => f.Id);
        return (anna, [.. facts.Select(f => byValue[f.Value])]);
    }

    [Fact]
    public async Task Nearest_with_oversample_covering_all_rows_equals_brute_force_cosine_order()
    {
        await using var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        db.At(1);
        var (_, ids) = await LikesAsync(db, "all", 200);
        var random = new Random(9);
        var vectors = ids.Select(_ => Random(random, 64)).ToArray();
        await db.Store.SetEmbeddingsAsync(Model, [.. ids.Select((id, i) => new FactEmbedding(id, vectors[i]))]);
        var query = Random(random, 64);

        var found = await db.Store.NearestAsync(Model, query, 10, Harness.Day(1), oversample: 200);

        var exact = ids.Select((id, i) => (Id: id, Cosine: Cosine(query, vectors[i]))).OrderByDescending(e => e.Cosine).ToArray();
        Assert.Equal(10, found.Count);
        Assert.Equal(found.Select(f => f.Similarity).OrderByDescending(s => s), found.Select(f => f.Similarity));
        var truth = exact.ToDictionary(e => e.Id, e => e.Cosine);
        foreach (var hit in found)
        {
            Assert.InRange(Math.Abs(hit.Similarity - truth[hit.Fact.Id]), 0, 0.01);
            Assert.True(truth[hit.Fact.Id] >= exact[9].Cosine - 0.01, "A returned fact is worse than the exact tenth by more than int8 rounding explains.");
        }
    }

    [Fact]
    public async Task Nearest_never_returns_a_fact_recorded_after_known_at_and_still_returns_one_retired_after_it()
    {
        await using var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        float[] kyiv = [1, 0, 0, 0], lviv = [0, 1, 0, 0];
        db.At(10);
        await db.IngestAsync("first", Harness.Lives(anna, "Kyiv", 0));
        var original = Assert.Single(await db.Store.HistoryAsync(anna, "lives_in")).Fact.Id;
        await db.Store.SetEmbeddingsAsync(Model, [new FactEmbedding(original, kyiv)]);
        db.At(20);
        await db.IngestAsync("second", Harness.Lives(anna, "Lviv", 15));
        var rows = await db.Store.HistoryAsync(anna, "lives_in");
        await db.Store.SetEmbeddingsAsync(Model, [.. rows.Select(r => new FactEmbedding(r.Fact.Id, r.Fact.Value == "Kyiv" ? kyiv : lviv))]);
        db.At(30);

        string[] Values(IReadOnlyList<ScoredFact> facts) => [.. facts.Select(f => f.Fact.Value)];
        var beforeSecond = await db.Store.NearestAsync(Model, lviv, 5, Harness.Day(16), Harness.Day(12));
        var afterSecond = await db.Store.NearestAsync(Model, lviv, 5, Harness.Day(16), Harness.Day(25));
        var beforeAnything = await db.Store.NearestAsync(Model, lviv, 5, Harness.Day(16), Harness.Day(5));
        var beforeItBegan = await db.Store.NearestAsync(Model, lviv, 5, Harness.Day(2), Harness.Day(25));

        Assert.Equal(["Kyiv"], Values(beforeSecond));
        Assert.Equal(original, beforeSecond.Single().Fact.Id);
        Assert.Equal(["Lviv"], Values(afterSecond));
        Assert.Empty(beforeAnything);
        Assert.Equal(["Kyiv"], Values(beforeItBegan));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store.NearestAsync(Model, lviv, 5, Harness.Day(16), Harness.Day(31)));
    }

    [Fact]
    public async Task Nearest_widens_the_candidate_set_when_filters_remove_most_of_it()
    {
        await using var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        var random = new Random(3);
        float[] target = [1, 1, 0, 0, 0, 0, 0, 0];
        db.At(1);
        var (_, visible) = await LikesAsync(db, "visible", 5);
        await db.Store.SetEmbeddingsAsync(Model, [.. visible.Select(id => new FactEmbedding(id, Random(random, 8)))]);
        db.At(10);
        var (_, hidden) = await LikesAsync(db, "hidden", 95, firstValue: 100);
        await db.Store.SetEmbeddingsAsync(Model, [.. hidden.Select(id => new FactEmbedding(id, new float[] { 1f + (float)random.NextDouble() / 10, 1f, 0, 0, 0, 0, 0, 0 }))]);

        var found = await db.Store.NearestAsync(Model, target, 5, Harness.Day(1), Harness.Day(5), oversample: 1);

        Assert.Equal(visible.Order(), found.Select(f => f.Fact.Id).Order());
    }

    [Fact]
    public async Task Embeddings_are_replaced_by_fact_and_model_and_refused_for_unknown_facts_or_other_lengths()
    {
        await using var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        db.At(1);
        var (_, ids) = await LikesAsync(db, "one", 3);
        await db.Store.SetEmbeddingsAsync(Model, [new FactEmbedding(ids[0], new float[] { 1, 0, 0, 0 })]);

        await db.Store.SetEmbeddingsAsync(Model, [new FactEmbedding(ids[0], new float[] { 0, 1, 0, 0 })]);
        var stranger = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.Store.SetEmbeddingsAsync(Model, [new FactEmbedding(ids[1], new float[] { 1, 0, 0, 0 }), new FactEmbedding(Guid.NewGuid(), new float[] { 1, 0, 0, 0 })]));
        var otherNamespace = new FactsStore(db.Data, "elsewhere", db.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => otherNamespace.SetEmbeddingsAsync(Model, [new FactEmbedding(ids[2], new float[] { 1, 0, 0, 0 })]));
        var length = await Assert.ThrowsAsync<PostgresException>(() => db.Store.NearestAsync(Model, new float[] { 1, 0, 0 }, 1, Harness.Day(1)));

        Assert.Contains("is not a row", stranger.Message, StringComparison.Ordinal);
        Assert.Equal("22026", length.SqlState);
        Assert.Equal((1, 1), (await db.CountAsync("fact_embeddings"), await db.CountAsync("fact_vectors")));
        Assert.Equal(ids[0], Assert.Single(await db.Store.NearestAsync(Model, new float[] { 0, 1, 0, 0 }, 1, Harness.Day(1))).Fact.Id);
        Assert.Empty(await db.Store.NearestAsync("another-model", new float[] { 0, 1, 0, 0 }, 1, Harness.Day(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.SetEmbeddingsAsync(Model, [new FactEmbedding(ids[1], new float[] { 0, 0 })]));
    }
}
