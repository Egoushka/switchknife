using System.Diagnostics;
using Npgsql;

namespace Switchknife.Facts.Tests;

/// <summary>Spec <c>docs/specs/2026-10-28-facts-write-path.md</c>, W1: closed ranges, entity objects, retraction, trust and changed sources.</summary>
public sealed class WritePathTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static async Task<(Harness Db, Guid Anna)> WithAnnaAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        await db.Store.DefinePredicateAsync("works_at", Cardinality.One);
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        return (db, await db.Store.EnsureEntityAsync("person", "Anna"));
    }

    private static NewFact Likes(Guid subject, string thing) => new(subject, "likes", thing, Harness.Day(0));

    private static NewFact LivesTo(Guid subject, string city, int fromDay, int toDay) => new(subject, "lives_in", city, Harness.Day(fromDay), Harness.Day(toDay));

    private static NewEpisode Episode(string source, string hash, EpisodeTrust trust = EpisodeTrust.Owner) => new("test", source, hash, Trust: trust);

    private static async Task<string?> ValueAt(Harness db, Guid subject, int day) =>
        (await db.Store.AsOfAsync(subject, "lives_in", Harness.Day(day))).SingleOrDefault()?.Value;

    [Fact]
    public async Task A_changed_source_retires_facts_it_no_longer_supports()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("S", "h1"), [Harness.Lives(anna, "Kyiv", 0), Likes(anna, "tea"), Likes(anna, "jazz")]);
        db.At(2);
        var second = await db.Store.IngestAsync(Episode("S", "h2"), [Harness.Lives(anna, "Kyiv", 0), Likes(anna, "tea")]);

        Assert.Equal((0, 2), (second.Written, second.Corroborated));
        Assert.Equal(["tea"], (await db.Store.AsOfAsync(anna, "likes", Harness.Day(3))).Select(f => f.Value));
        var jazz = Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Value == "jazz");
        Assert.Equal(("source_changed", (DateTimeOffset?)Harness.Day(2)), (jazz.RetiredReason, jazz.RetiredAt));
        Assert.Equal("Kyiv", await ValueAt(db, anna, 3));
        var kyiv = Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(3)));
        Assert.Equal(2, await Count(db, $"select count(*) from facts.fact_sources where fact_id = '{kyiv.Id}'"));

        var counts = await Counts(db);
        Assert.True((await db.Store.IngestAsync(Episode("S", "h2"), [])).Duplicate);
        Assert.Equal(counts, await Counts(db));
    }

    [Fact]
    public async Task A_fact_with_another_source_survives_a_changed_source()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("S", "h1"), [Likes(anna, "jazz")]);
        db.At(2);
        await db.Store.IngestAsync(Episode("T", "t1"), [Likes(anna, "jazz")]);
        db.At(3);
        await db.Store.IngestAsync(Episode("S", "h2"), []);

        var jazz = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(4)));
        Assert.Equal(1, await Count(db, $"select count(*) from facts.fact_sources s join facts.episodes e on e.id = s.episode_id where s.fact_id = '{jazz.Id}' and e.source_ref = 'T'"));
    }

    [Fact]
    public async Task A_changed_source_leaves_a_gap_it_does_not_invent_a_value()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("S", "h1"), [Harness.Lives(anna, "Kyiv", 0)]);
        db.At(2);
        await db.Store.IngestAsync(Episode("S", "h2"), [Harness.Lives(anna, "Kyiv", 0), Harness.Lives(anna, "Lviv", 10)]);

        Assert.Equal("Kyiv", await ValueAt(db, anna, 5));
        Assert.Equal("Lviv", await ValueAt(db, anna, 15));

        db.At(3);
        await db.Store.IngestAsync(Episode("S", "h3"), []);
        Assert.Null(await ValueAt(db, anna, 5));
        Assert.Null(await ValueAt(db, anna, 15));
        Assert.Equal("Kyiv", (await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(5), Harness.Day(2))).Single().Value);
        Assert.Equal("Lviv", (await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(15), Harness.Day(2))).Single().Value);
    }

    [Fact]
    public async Task A_retracted_fact_is_not_extracted_again()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("S1", "a"), [Likes(anna, "tea"), Harness.Lives(anna, "Kyiv", 0), Harness.Lives(anna, "Lviv", 10)]);
        var tea = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(1)));
        var lviv = Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(11)));

        db.At(2);
        Assert.Equal(1, await db.Store.RetractAsync(tea.Id));
        Assert.Equal(1, await db.Store.RetractAsync(lviv.Id));
        Assert.Equal(0, await db.Store.RetractAsync(tea.Id));
        Assert.Equal(0, await db.Store.RetractAsync(Guid.NewGuid()));

        db.At(3);
        var again = await db.Store.IngestAsync(Episode("S2", "b"), [Likes(anna, "tea"), Harness.Lives(anna, "Lviv", 10)]);

        Assert.Equal((0, 0), (again.Written, again.Corroborated));
        Assert.Empty(await db.Store.AsOfAsync(anna, "likes", Harness.Day(4)));
        Assert.Null(await ValueAt(db, anna, 11));
        Assert.Equal("Kyiv", await ValueAt(db, anna, 5));
        Assert.Equal("Lviv", (await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(11), Harness.Day(1))).Single().Value);
    }

    [Fact]
    public async Task Restoring_a_retracted_fact_makes_it_live_and_extractable_again()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("S1", "a"), [Likes(anna, "tea"), Harness.Lives(anna, "Lviv", 10)]);
        var tea = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(1)));
        var lviv = Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(11)));
        db.At(2);
        await db.Store.RetractAsync(tea.Id);
        await db.Store.RetractAsync(lviv.Id);

        db.At(3);
        Assert.Equal(1, await db.Store.RestoreAsync(tea.Id));
        Assert.Equal(0, await db.Store.RestoreAsync(tea.Id));
        var restored = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(4)));
        Assert.Equal((tea.Text, tea.ValidFrom), (restored.Text, restored.ValidFrom));
        Assert.NotEqual(tea.Id, restored.Id);
        Assert.Equal(1, await Count(db, $"select count(*) from facts.fact_sources where fact_id = '{restored.Id}'"));
        Assert.Equal(2, (await db.Store.HistoryAsync(anna, "likes")).Count);
        Assert.Equal(1, (await db.Store.IngestAsync(Episode("S2", "b"), [Likes(anna, "tea")])).Corroborated);

        await db.Store.IngestAsync(Episode("S3", "c"), [Harness.Lives(anna, "Paris", 12)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.RestoreAsync(lviv.Id));
        Assert.Equal("Paris", await ValueAt(db, anna, 13));
    }

    [Fact]
    public async Task A_third_party_episode_cannot_retire_an_owner_fact()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("mine", "a"), [Harness.Lives(anna, "Lviv", 0)]);
        db.At(2);
        var result = await db.Store.IngestAsync(Episode("stranger", "b", EpisodeTrust.ThirdParty), [Harness.Lives(anna, "Kyiv", 10)]);

        Assert.Equal((0, 1), (result.Written, result.Outranked));
        db.At(45);
        var history = await db.Store.HistoryAsync(anna, "lives_in");
        var kyiv = Assert.Single(history, v => v.Fact.Value == "Kyiv");
        Assert.Equal(("outranked", (DateTimeOffset?)Harness.Day(2)), (kyiv.RetiredReason, kyiv.RetiredAt));
        var lviv = Assert.Single(history, v => v.Fact.Value == "Lviv");
        Assert.Null(lviv.RetiredAt);
        Assert.Null(lviv.Fact.ValidTo);
        for (var known = 1; known <= 40; known += 3)
        {
            for (var valid = 0; valid <= 30; valid += 2)
            {
                var at = await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(valid), Harness.Day(known));
                Assert.DoesNotContain(at, f => f.Value == "Kyiv");
            }
        }
    }

    [Fact]
    public async Task An_owner_episode_retires_a_third_party_fact()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("stranger", "a", EpisodeTrust.ThirdParty), [Harness.Lives(anna, "Kyiv", 0)]);
        db.At(2);
        var result = await db.Store.IngestAsync(Episode("mine", "b"), [Harness.Lives(anna, "Lviv", 5)]);

        Assert.Equal((1, 0), (result.Written, result.Outranked));
        Assert.Equal("Kyiv", await ValueAt(db, anna, 3));
        Assert.Equal("Lviv", await ValueAt(db, anna, 6));
    }

    [Fact]
    public async Task A_third_party_fact_the_owner_confirms_gains_owner_trust()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("stranger", "a", EpisodeTrust.ThirdParty), [Harness.Lives(anna, "Kyiv", 0)]);
        db.At(2);
        await db.Store.IngestAsync(Episode("mine", "b"), [Harness.Lives(anna, "Kyiv", 0)]);
        db.At(3);
        var result = await db.Store.IngestAsync(Episode("other", "c", EpisodeTrust.ThirdParty), [Harness.Lives(anna, "Paris", 10)]);

        Assert.Equal(1, result.Outranked);
        Assert.Equal("Kyiv", await ValueAt(db, anna, 11));
    }

    [Fact]
    public async Task A_closed_range_on_a_one_predicate_keeps_the_tail_of_the_containing_row()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        var boris = await db.Store.EnsureEntityAsync("person", "Boris");
        var clara = await db.Store.EnsureEntityAsync("person", "Clara");
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [Harness.Lives(anna, "Lviv", 0), Harness.Lives(boris, "Lviv", 0), Harness.Lives(boris, "Paris", 30), Harness.Lives(clara, "Paris", 30)]);
        db.At(2);
        await db.Store.IngestAsync(Episode("b", "b"), [LivesTo(anna, "Kyiv", 10, 20), LivesTo(boris, "Kyiv", 10, 40), LivesTo(clara, "Kyiv", 10, 40)]);

        // Inside an open row: the old value resumes.
        Assert.Equal(["Lviv", "Kyiv", "Lviv"], new[] { await ValueAt(db, anna, 5), await ValueAt(db, anna, 15), await ValueAt(db, anna, 25) });
        var lviv = (await db.Store.HistoryAsync(anna, "lives_in")).Where(v => v.Fact.Value == "Lviv" && v.RetiredAt is null).ToArray();
        Assert.Equal(2, lviv.Length);
        Assert.Equal(1, await Count(db, $"select count(distinct fingerprint) from facts.facts where id in ('{lviv[0].Fact.Id}', '{lviv[1].Fact.Id}')"));

        // Ending after the row ends: it ends with the row, nothing resumes.
        Assert.Equal(["Lviv", "Kyiv", "Paris"], new[] { await ValueAt(db, boris, 5), await ValueAt(db, boris, 25), await ValueAt(db, boris, 35) });
        Assert.Equal((DateTimeOffset?)Harness.Day(30), (await db.Store.AsOfAsync(boris, "lives_in", Harness.Day(25))).Single().ValidTo);

        // In a gap before a known value: it ends where that value begins.
        Assert.Equal([null, "Kyiv", "Paris"], new[] { await ValueAt(db, clara, 5), await ValueAt(db, clara, 25), await ValueAt(db, clara, 35) });
        Assert.Equal(2, (await db.Store.HistoryAsync(clara, "lives_in")).Count);
    }

    [Fact]
    public async Task Entity_valued_objects_supersede_and_corroborate_by_entity()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        var acme = await db.Store.EnsureEntityAsync("org", "Acme");
        var globex = await db.Store.EnsureEntityAsync("org", "Globex");
        NewFact WorksAt(Guid org, string name, int day) => new(anna, "works_at", null, Harness.Day(day), Text: $"Anna works at {name}", ObjectEntity: org);
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [WorksAt(acme, "Acme", 0)]);
        db.At(2);
        Assert.Equal(1, (await db.Store.IngestAsync(Episode("b", "b"), [WorksAt(acme, "Acme", 0)])).Corroborated);
        Assert.Equal(1, (await db.Store.IngestAsync(Episode("c", "c"), [WorksAt(globex, "Globex", 10)])).Written);

        var before = Assert.Single(await db.Store.AsOfAsync(anna, "works_at", Harness.Day(5)));
        var after = Assert.Single(await db.Store.AsOfAsync(anna, "works_at", Harness.Day(15)));
        Assert.Equal((acme, string.Empty), (before.ObjectEntity, before.Value));
        Assert.Equal((globex, "Anna works at Globex"), (after.ObjectEntity, after.Text));
        Assert.Equal(2, await Count(db, $"select count(*) from facts.fact_sources where fact_id = '{before.Id}'"));

        var episode = Episode("d", "d");
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.IngestAsync(episode, [new NewFact(anna, "works_at", "Acme", Harness.Day(0), Text: "x", ObjectEntity: acme)]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.IngestAsync(episode, [new NewFact(anna, "works_at", null, Harness.Day(0), Text: "x")]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.IngestAsync(episode, [new NewFact(anna, "works_at", null, Harness.Day(0), ObjectEntity: acme)]));
        Assert.Equal(3, await db.CountAsync("episodes"));
    }

    [Fact]
    public async Task Two_live_copies_of_one_piece_cannot_exist_by_raw_sql()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [Harness.Lives(anna, "Lviv", 0)]);
        await db.Store.IngestAsync(Episode("b", "b"), [LivesTo(anna, "Kyiv", 10, 20)]);
        Assert.Equal(2, await Count(db, "select count(*) from facts.facts where object_value = 'Lviv' and retired_at is null"));

        const string copy =
            """
            insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, slot, fingerprint, episode_id)
            select gen_random_uuid(), namespace, subject_id, predicate, object_value, text, valid, recorded_at, 'other-slot', fingerprint, episode_id
            from facts.facts where object_value = 'Lviv' and retired_at is null and upper(valid) is null
            """;
        await using var command = db.Data.CreateCommand(copy);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    [Fact]
    public async Task The_write_path_adds_no_per_fact_round_trip()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        var cities = Enumerable.Range(0, 5).Select(i => Harness.Lives(anna, $"city {i}", i)).ToArray();
        var things = Enumerable.Range(0, 5).Select(i => Likes(anna, $"thing {i}")).ToArray();

        var one = await RoundTripsAsync(db, () => db.Store.IngestAsync(Episode("one", "1"), cities));
        var many = await RoundTripsAsync(db, () => db.Store.IngestAsync(Episode("many", "1"), things));

        // Before this slice: one episode statement, one lock statement and two per fact (12); one episode statement and one per fact (6).
        // A source with earlier versions pays two more statements (lock their slots, retire what they alone support); a first version pays none.
        Assert.True(one <= 12, $"one: {one}");
        Assert.True(many <= 6, $"many: {many}");
    }

    /// <summary>The statements (a batch counts once) Npgsql sent to <paramref name="db"/>'s database while <paramref name="action"/> ran; other tests' traffic is filtered out by database.</summary>
    private static async Task<int> RoundTripsAsync(Harness db, Func<Task> action)
    {
        var database = new NpgsqlConnectionStringBuilder(db.Data.ConnectionString).Database;
        var count = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if ((a.GetTagItem("db.namespace") ?? a.GetTagItem("db.name")) as string == database)
                {
                    Interlocked.Increment(ref count);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await action();
        return count;
    }

    private static async Task<long> Count(Harness db, string sql)
    {
        await using var command = db.Data.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long[]> Counts(Harness db) =>
        [await db.CountAsync("entities"), await db.CountAsync("episodes"), await db.CountAsync("facts"), await db.CountAsync("fact_sources")];
}
