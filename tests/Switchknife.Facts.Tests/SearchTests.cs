namespace Switchknife.Facts.Tests;

/// <summary>Spec <c>docs/specs/2026-10-29-facts-explain-fusion.md</c>, S: full-text and vector results fused by reciprocal rank.</summary>
public sealed class SearchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Model = "test-model";
    private const string Word = "marker";

    /// <summary>
    /// A fact the fixture writes. <paramref name="Reps"/> repeats the search word in its text (0: the text lacks it), and a higher count
    /// ranks higher in the text list; <paramref name="Angle"/> is its embedding's angle in degrees from the query <c>[1, 0]</c>
    /// (null: no embedding), and a smaller angle ranks higher in the vector list.
    /// </summary>
    private sealed record Spec(string Name, int Reps, int? Angle);

    private static readonly float[] Query = [1, 0];

    private static float[] AtAngle(int degrees) => [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];

    private static string TextOf(Spec spec) => spec.Reps == 0 ? spec.Name : string.Join(' ', Enumerable.Repeat(Word, spec.Reps)) + " " + spec.Name;

    private static async Task<(Harness Db, Guid Anna)> NewAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        return (db, await db.Store.EnsureEntityAsync("person", "Anna"));
    }

    /// <summary>Writes the specs as one episode at the clock and embeds those with an angle; returns their ids by name.</summary>
    private static async Task<Dictionary<string, Guid>> SeedAsync(Harness db, FactsStore store, Guid subject, string source, params Spec[] specs)
    {
        await store.IngestAsync(
            new NewEpisode("test", source, source),
            [.. specs.Select(s => new NewFact(subject, "likes", s.Name, Harness.Day(0), Text: TextOf(s)))]);
        var ids = (await store.AsOfAsync(subject, "likes", Harness.Day(1))).ToDictionary(f => f.Value, f => f.Id);
        await store.SetEmbeddingsAsync(Model, [.. specs.Where(s => s.Angle is not null).Select(s => new FactEmbedding(ids[s.Name], AtAngle(s.Angle!.Value)))]);
        return ids;
    }

    private static string[] Names(IReadOnlyList<SearchHit> hits) => [.. hits.Select(h => h.Fact.Value)];

    [Fact]
    public async Task Facts_recorded_after_known_at_never_appear_in_search()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(10);
        var first = await SeedAsync(db, db.Store, anna, "first", new Spec("old", 1, 5));
        db.At(20);
        await SeedAsync(db, db.Store, anna, "second", new Spec("new", 1, 6));
        db.At(30);
        await db.Store.RetractAsync(first["old"]);
        db.At(40);

        foreach (var (text, vector) in new (string?, float[])[] { (Word, []), (null, Query), (Word, Query) })
        {
            Assert.Empty(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(40), knownAt: Harness.Day(5)));
            Assert.Equal(["old"], Names(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(40), knownAt: Harness.Day(15))));
            Assert.Equal(["old", "new"], Names(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(40), knownAt: Harness.Day(25))).Order().Reverse());
            Assert.Equal(["new"], Names(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(40), knownAt: Harness.Day(35))));
        }
    }

    [Fact]
    public async Task Facts_not_valid_at_the_instant_never_appear_in_search()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(
            new NewEpisode("test", "s", "s"), [new NewFact(anna, "likes", "brief", Harness.Day(5), Harness.Day(10), Text: $"{Word} brief")]);
        var id = (await db.Store.AsOfAsync(anna, "likes", Harness.Day(6))).Single().Id;
        await db.Store.SetEmbeddingsAsync(Model, [new FactEmbedding(id, Query)]);
        db.At(20);

        foreach (var (text, vector) in new (string?, float[])[] { (Word, []), (null, Query), (Word, Query) })
        {
            Assert.Empty(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(3)));
            Assert.Equal(["brief"], Names(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(7))));
            Assert.Empty(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(10)));
            Assert.Empty(await db.Store.SearchAsync(text, vector, Model, validAt: Harness.Day(12)));
        }
    }

    [Fact]
    public async Task Fusion_ranks_a_fact_found_by_both_above_a_fact_found_by_one()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);
        await SeedAsync(
            db, db.Store, anna, "s",
            new Spec("textFirst", 5, null),
            new Spec("textSecond", 4, 50),
            new Spec("both", 3, 15),
            new Spec("vectorFirst", 0, 5),
            new Spec("filler", 1, 40));

        var hits = await db.Store.SearchAsync(Word, Query, Model, validAt: Harness.Day(1));

        var both = hits.Single(h => h.Fact.Value == "both");
        Assert.Equal((3, 2), (both.TextRank, both.VectorRank));
        Assert.Equal("both", hits[0].Fact.Value);
        Assert.True(hits.Single(h => h.Fact.Value == "textFirst").Score < both.Score);
        Assert.True(hits.Single(h => h.Fact.Value == "vectorFirst").Score < both.Score);
    }

    [Fact]
    public async Task Text_only_and_vector_only_searches_work()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);
        await SeedAsync(
            db, db.Store, anna, "s",
            new Spec("t1", 4, null),
            new Spec("t2", 3, 20),
            new Spec("t3", 2, 10),
            new Spec("v1", 0, 5),
            new Spec("v2", 0, 30));

        var textOnly = await db.Store.SearchAsync(Word, default, null, validAt: Harness.Day(1));
        var blankText = await db.Store.SearchAsync("  ", Query, Model, validAt: Harness.Day(1));
        var vectorOnly = await db.Store.SearchAsync(null, Query, Model, validAt: Harness.Day(1));
        var emptyVector = await db.Store.SearchAsync(Word, ReadOnlyMemory<float>.Empty, Model, validAt: Harness.Day(1));

        Assert.Equal(["t1", "t2", "t3"], Names(textOnly));
        Assert.Equal([1, 2, 3], textOnly.Select(h => h.TextRank));
        Assert.All(textOnly, h => Assert.Null(h.VectorRank));
        Assert.All(textOnly, h => Assert.Null(h.Similarity));
        Assert.Equal(["v1", "t3", "t2", "v2"], Names(vectorOnly));
        Assert.Equal([1, 2, 3, 4], vectorOnly.Select(h => h.VectorRank));
        Assert.All(vectorOnly, h => Assert.Null(h.TextRank));
        Assert.Equal(Names(vectorOnly), Names(blankText));
        Assert.Equal(Names(textOnly), Names(emptyVector));
        Assert.Equal(vectorOnly.Select(h => h.Similarity).Order().Reverse(), vectorOnly.Select(h => h.Similarity));
    }

    [Fact]
    public async Task Search_never_returns_retired_or_other_namespace_facts()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);
        var ids = await SeedAsync(db, db.Store, anna, "s", new Spec("kept", 2, 5), new Spec("retracted", 3, 6));
        await db.Store.IngestAsync(new NewEpisode("test", "a", "a"), [new NewFact(anna, "lives_in", "Lviv", Harness.Day(0), Text: $"{Word} lives in Lviv")]);
        db.At(2);
        await db.Store.IngestAsync(new NewEpisode("test", "b", "b"), [new NewFact(anna, "lives_in", "Kyiv", Harness.Day(10), Text: $"{Word} lives in Kyiv")]);
        await db.Store.RetractAsync(ids["retracted"]);
        var elsewhere = new FactsStore(db.Data, "elsewhere", db.Clock);
        await elsewhere.DefinePredicateAsync("likes", Cardinality.Many);
        var bob = await elsewhere.EnsureEntityAsync("person", "Bob");
        await SeedAsync(db, elsewhere, bob, "x", new Spec("foreign", 9, 1));
        db.At(3);

        var late = await db.Store.SearchAsync(Word, Query, Model, validAt: Harness.Day(20));
        var early = await db.Store.SearchAsync(Word, Query, Model, validAt: Harness.Day(5));

        Assert.Equal(["Kyiv", "kept"], Names(late).Order(StringComparer.Ordinal));
        Assert.Equal(["Lviv", "kept"], Names(early).Order(StringComparer.Ordinal));
        await using var live = db.Data.CreateCommand("select id from facts.facts where retired_at is null");
        var liveIds = new HashSet<Guid>();
        await using (var reader = await live.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                liveIds.Add(reader.GetGuid(0));
            }
        }

        Assert.All(late.Concat(early), h => Assert.Contains(h.Fact.Id, liveIds));
    }

    [Fact]
    public async Task Score_is_the_sum_of_reciprocal_ranks_and_ties_break_by_better_rank_then_id()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);
        await SeedAsync(
            db, db.Store, anna, "s",
            new Spec("a", 6, null),
            new Spec("b", 5, 12),
            new Spec("c", 4, 3),
            new Spec("d", 3, null),
            new Spec("e", 2, 9),
            new Spec("f", 0, 6),
            new Spec("g", 0, 15));
        var textOnly = await db.Store.SearchAsync(Word, default, null, validAt: Harness.Day(1));
        var vectorOnly = await db.Store.SearchAsync(null, Query, Model, validAt: Harness.Day(1));
        Assert.Equal(["a", "b", "c", "d", "e"], Names(textOnly));
        Assert.Equal(["c", "f", "e", "b", "g"], Names(vectorOnly));

        var fused = await db.Store.SearchAsync(Word, Query, Model, validAt: Harness.Day(1));

        var textRank = textOnly.ToDictionary(h => h.Fact.Id, h => h.TextRank!.Value);
        var vectorRank = vectorOnly.ToDictionary(h => h.Fact.Id, h => h.VectorRank!.Value);
        foreach (var hit in fused)
        {
            var expected = (textRank.TryGetValue(hit.Fact.Id, out var t) ? 1.0 / (60 + t) : 0) + (vectorRank.TryGetValue(hit.Fact.Id, out var v) ? 1.0 / (60 + v) : 0);
            Assert.Equal(expected, hit.Score, 12);
            Assert.Equal(textRank.TryGetValue(hit.Fact.Id, out var t2) ? t2 : null, hit.TextRank);
            Assert.Equal(vectorRank.TryGetValue(hit.Fact.Id, out var v2) ? v2 : null, hit.VectorRank);
        }

        var order = fused
            .OrderByDescending(h => h.Score)
            .ThenBy(h => Math.Min(h.TextRank ?? int.MaxValue, h.VectorRank ?? int.MaxValue))
            .ThenBy(h => h.Fact.Id.ToString(), StringComparer.Ordinal)
            .Select(h => h.Fact.Id);
        Assert.Equal(order, fused.Select(h => h.Fact.Id));
        Assert.Equal(7, fused.Count);
        Assert.Equal(Names(fused).Distinct().Count(), fused.Count);
    }

    [Fact]
    public async Task Equal_scores_with_equal_best_ranks_order_by_id()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);
        var vectorSide = await SeedAsync(db, db.Store, anna, "v", new Spec("vectorOne", 0, 3), new Spec("vectorTwo", 0, 6));
        db.At(2);
        var textSide = await SeedAsync(db, db.Store, anna, "t", new Spec("textOne", 9, null), new Spec("textTwo", 8, null));

        var hits = await db.Store.SearchAsync(Word, Query, Model, validAt: Harness.Day(2));

        // vectorOne and textOne are both first in their list (1/61), vectorTwo and textTwo both second (1/62). Ids follow creation time.
        Assert.Equal(hits[0].Score, hits[1].Score);
        Assert.Equal(["vectorOne", "textOne", "vectorTwo", "textTwo"], Names(hits));
        Assert.True(string.CompareOrdinal(vectorSide["vectorOne"].ToString(), textSide["textOne"].ToString()) < 0);
    }

    [Fact]
    public async Task Equal_scores_go_to_the_fact_with_the_better_rank_before_the_id()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);

        // q is 62nd in both lists: 1/122 + 1/122 is exactly 1/61, the score of p, which is first in the text list only. q is the older row, so the smaller id.
        // Sixty facts in both lists (text 2 to 61, vector 1 to 60) outscore both; one vector-only fact sits between them and q in the vector list.
        var q = await SeedAsync(db, db.Store, anna, "q", new Spec("q", 1, 80));
        db.At(2);
        var rest = Enumerable.Range(0, 60).Select(i => new Spec($"both{i:00}", 2, 3))
            .Append(new Spec("vectorOnly", 0, 40))
            .Append(new Spec("p", 3, null))
            .ToArray();
        var p = await SeedAsync(db, db.Store, anna, "rest", rest);
        Assert.True(string.CompareOrdinal(q["q"].ToString(), p["p"].ToString()) < 0);

        var hits = await db.Store.SearchAsync(Word, Query, Model, 70, Harness.Day(2));

        var qHit = hits.Single(h => h.Fact.Value == "q");
        Assert.Equal((62, 62), (qHit.TextRank, qHit.VectorRank));
        Assert.Equal(63, hits.Count);
        Assert.Equal(hits[60].Score, hits[61].Score);
        Assert.Equal(["p", "q", "vectorOnly"], Names(hits).Skip(60));
    }

    [Fact]
    public async Task A_fact_ranked_beyond_limit_in_both_lists_rises_into_the_results()
    {
        var (db, anna) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);
        var specs = Enumerable.Range(1, 11).Select(i => new Spec($"a{i:00}", 40 - i, null))
            .Concat(Enumerable.Range(1, 11).Select(i => new Spec($"b{i:00}", 0, 3 * i)))
            .Append(new Spec("middle", 20, 36))
            .ToArray();
        await SeedAsync(db, db.Store, anna, "s", specs);
        var textOnly = await db.Store.SearchAsync(Word, default, null, 30, validAt: Harness.Day(1));
        var vectorOnly = await db.Store.SearchAsync(null, Query, Model, 30, validAt: Harness.Day(1));
        Assert.Equal(12, textOnly.Single(h => h.Fact.Value == "middle").TextRank);
        Assert.Equal(12, vectorOnly.Single(h => h.Fact.Value == "middle").VectorRank);

        var hits = await db.Store.SearchAsync(Word, Query, Model, 10, Harness.Day(1));

        Assert.Equal(10, hits.Count);
        Assert.Equal("middle", hits[0].Fact.Value);
        Assert.Equal((12, 12), (hits[0].TextRank, hits[0].VectorRank));
    }

    [Fact]
    public async Task Search_rejects_bad_input()
    {
        var (db, _) = await NewAsync(postgres);
        await using var _ = db;
        db.At(1);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => db.Store.SearchAsync(null, default, Model));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => db.Store.SearchAsync(" ", default, Model));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => db.Store.SearchAsync(Word, Query, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store.SearchAsync(Word, default, null, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store.SearchAsync(Word, default, null, 1001));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store.SearchAsync(Word, default, null, knownAt: Harness.Day(2)));
        Assert.Empty(await db.Store.SearchAsync(Word, default, null, 1000));
        Assert.Empty(await db.Store.SearchAsync("!!! ???", default, null));
    }
}
