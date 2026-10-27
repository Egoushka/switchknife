using Npgsql;
using Switchknife.Facts.Bench;

namespace Switchknife.Facts.Tests;

public sealed class FactsStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static async Task<(Harness Db, Guid Anna)> WithAnnaAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        await db.Store.DefinePredicateAsync("met", Cardinality.Event);
        return (db, await db.Store.EnsureEntityAsync("person", "Anna"));
    }

    [Fact]
    public async Task As_of_valid_time_returns_the_value_the_oracle_expects()
    {
        await using var data = await postgres.NewEmptyAsync();

        var report = await Runner.RunAsync(data, new RunOptions(Subjects: 60, Facts: 3000, Queries: 2000, EpisodeSize: 5, VectorDimensions: 0, Seed: 11), fresh: false);

        Assert.Equal(0, report.AsOfWrong);
        Assert.Equal(0, report.AsOfOverlap);
        Assert.True(report.RetiredRows > 0, "Superseding must retire rows, not overwrite them.");
    }

    [Fact]
    public async Task As_of_known_time_does_not_see_later_learning()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(10);
        await db.IngestAsync("first", Harness.Lives(anna, "Kyiv", 0));
        db.At(20);
        await db.IngestAsync("second", Harness.Lives(anna, "Lviv", 15));

        db.At(25);
        string? Answer(IReadOnlyList<Fact> facts) => facts.SingleOrDefault()?.Value;
        var beforeLearning = await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(16), Harness.Day(5));
        var betweenEpisodes = await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(16), Harness.Day(12));
        var afterBoth = await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(16), Harness.Day(22));
        var now = await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(16));

        Assert.Null(Answer(beforeLearning));
        Assert.Equal("Kyiv", Answer(betweenEpisodes));
        Assert.Equal("Lviv", Answer(afterBoth));
        Assert.Equal("Lviv", Answer(now));
    }

    [Fact]
    public async Task A_new_value_retires_the_old_row_and_keeps_both()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(10);
        await db.IngestAsync("first", Harness.Lives(anna, "Kyiv", 0));
        db.At(20);
        await db.IngestAsync("second", Harness.Lives(anna, "Lviv", 15));

        var history = await db.Store.HistoryAsync(anna, "lives_in");

        Assert.Equal(3, history.Count);
        var original = Assert.Single(history, v => v.RetiredReason == "superseded");
        var successor = Assert.Single(history, v => v.Fact.Value == "Lviv");
        var narrowed = Assert.Single(history, v => v.Fact.Value == "Kyiv" && v.RetiredAt is null);
        Assert.Equal(successor.Fact.Id, original.SupersededBy);
        Assert.Equal((Harness.Day(0), (DateTimeOffset?)null), (original.Fact.ValidFrom, original.Fact.ValidTo));
        Assert.Equal((Harness.Day(0), (DateTimeOffset?)Harness.Day(15)), (narrowed.Fact.ValidFrom, narrowed.Fact.ValidTo));
        Assert.Equal((Harness.Day(15), (DateTimeOffset?)null), (successor.Fact.ValidFrom, successor.Fact.ValidTo));
        Assert.Equal(Harness.Day(20), original.RetiredAt);
        Assert.Equal(await db.CountAsync("facts"), await db.CountAsync("fact_sources"));
    }

    [Fact]
    public async Task A_fact_learned_late_ends_where_the_next_known_value_begins()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(20);
        await db.IngestAsync("later", Harness.Lives(anna, "Lviv", 15));
        db.At(30);
        await db.IngestAsync("earlier", Harness.Lives(anna, "Kyiv", 0));

        var history = await db.Store.HistoryAsync(anna, "lives_in");

        Assert.Equal(2, history.Count);
        Assert.All(history, v => Assert.Null(v.RetiredAt));
        for (var day = 0; day <= 30; day++)
        {
            var value = Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(day))).Value;
            Assert.Equal(day < 15 ? "Kyiv" : "Lviv", value);
        }
    }

    [Fact]
    public async Task Two_live_values_for_one_slot_cannot_overlap_even_by_raw_sql()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        await db.IngestAsync("seed");
        var episode = (Guid)(await Scalar(db, "select id from facts.episodes limit 1"))!;
        const string Insert =
            """
            insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, slot, fingerprint, episode_id)
            values (gen_random_uuid(), 'test', $1, 'lives_in', 'x', 'lives in x', tstzrange('2026-01-01', null, '[)'), now(), 'slot-1', $2, $3)
            """;

        await Execute(db, Insert, anna, "a", episode);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(db, Insert, anna, "b", episode));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, error.SqlState);
    }

    [Fact]
    public async Task Many_and_event_predicates_are_never_superseded_by_rule()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(5);
        await db.IngestAsync(
            "one",
            new NewFact(anna, "likes", "tea", Harness.Day(0)),
            new NewFact(anna, "likes", "coffee", Harness.Day(1)),
            new NewFact(anna, "met", "Boris", Harness.Day(2)),
            new NewFact(anna, "met", "Boris", Harness.Day(3)));
        var again = await db.IngestAsync("two", new NewFact(anna, "likes", " Tea ", Harness.Day(4)));

        var now = await db.Store.AsOfAsync(anna, null, Harness.Day(3), Harness.Day(5));
        var history = await db.Store.HistoryAsync(anna, "likes");

        Assert.Equal((0, 1), (again.Written, again.Corroborated));
        Assert.Equal(["coffee", "tea"], now.Where(f => f.Predicate == "likes").Select(f => f.Value).Order());
        Assert.Equal(2, history.Count);
        Assert.All(history, v => Assert.Null(v.RetiredAt));
        Assert.Equal(2, await CountLive(db, "met"));
        Assert.Single(await db.Store.AsOfAsync(anna, "met", Harness.Day(2)));
    }

    [Fact]
    public async Task Ingesting_the_same_episode_twice_changes_nothing()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        var facts = new[] { Harness.Lives(anna, "Kyiv", 0), new NewFact(anna, "likes", "tea", Harness.Day(0)) };
        var first = await db.IngestAsync("same", facts);
        var before = await Counts(db);

        var second = await db.IngestAsync("same", facts);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.EpisodeId, second.EpisodeId);
        Assert.Equal((0, 0), (second.Written, second.Corroborated));
        Assert.Equal(before, await Counts(db));
    }

    [Fact]
    public async Task The_same_value_again_only_adds_a_source()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        await db.IngestAsync("first", Harness.Lives(anna, "Kyiv", 0));

        var again = await db.IngestAsync("second", Harness.Lives(anna, "kyiv", 40));

        Assert.Equal((0, 1), (again.Written, again.Corroborated));
        var row = Assert.Single(await db.Store.HistoryAsync(anna, "lives_in"));
        Assert.Equal(2, await CountSources(db, row.Fact.Id));
    }

    [Fact]
    public async Task A_value_beginning_where_another_begins_replaces_it()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.IngestAsync("first", Harness.Lives(anna, "Kyiv", 10));
        db.At(2);
        await db.IngestAsync("second", Harness.Lives(anna, "Lviv", 10));

        var history = await db.Store.HistoryAsync(anna, "lives_in");

        Assert.Equal(2, history.Count);
        Assert.Equal("Lviv", Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(11))).Value);
        Assert.Equal("Kyiv", Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(11), Harness.Day(1))).Value);
    }

    [Fact]
    public async Task A_many_fact_with_an_end_is_a_closed_range()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        await db.IngestAsync("first", new NewFact(anna, "likes", "tea", Harness.Day(0), Harness.Day(10)));

        Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(9)));
        Assert.Empty(await db.Store.AsOfAsync(anna, "likes", Harness.Day(10)));
    }

    [Fact]
    public async Task Instants_finer_than_a_microsecond_do_not_split_one_assertion()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        var from = Harness.Day(3).AddTicks(7);
        await db.IngestAsync("first", new NewFact(anna, "lives_in", "Kyiv", from));

        var again = await db.IngestAsync("second", new NewFact(anna, "lives_in", "Kyiv", from));

        Assert.Equal((0, 1), (again.Written, again.Corroborated));
        Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", from));
    }

    [Fact]
    public async Task An_external_ref_never_gets_a_second_entity()
    {
        await using var db = await postgres.NewAsync();

        var ids = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var seen = new List<Guid>();
            for (var i = 0; i < 10; i++)
            {
                seen.Add(await db.Store.EnsureEntityAsync("person", "Anna", "nytka:people/7"));
            }

            return seen;
        }));

        Assert.Single(ids.SelectMany(i => i).Distinct());
        Assert.Equal(1, await db.CountAsync("entities"));
    }

    [Fact]
    public async Task Ingest_inside_a_caller_transaction_rolls_back_with_it()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        await using var connection = await db.Data.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var result = await db.Store.IngestAsync(connection, transaction, new NewEpisode("test", "rolled-back", "h"), [Harness.Lives(anna, "Kyiv", 0)]);
        var inside = (long)(await new NpgsqlCommand("select count(*) from facts.facts", connection, transaction).ExecuteScalarAsync())!;
        await transaction.RollbackAsync();

        Assert.Equal(1, result.Written);
        Assert.Equal(1, inside);
        Assert.Equal((0, 0), (await db.CountAsync("episodes"), await db.CountAsync("facts")));
    }

    [Fact]
    public async Task Omitting_known_at_means_now_and_a_future_known_at_is_refused()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(10);
        await db.IngestAsync("first", Harness.Lives(anna, "Kyiv", 0));

        Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(1)));
        Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(1), Harness.Day(10)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store.AsOfAsync(anna, "lives_in", Harness.Day(1), Harness.Day(11)));
        Assert.Empty(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(1), Harness.Day(9)));
    }

    [Fact]
    public async Task Recorded_at_comes_from_the_ingest_clock_not_the_episode_time()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(10);

        await db.Store.IngestAsync(new NewEpisode("test", "old-call", "h", OccurredAt: Harness.Day(-1000)), [Harness.Lives(anna, "Kyiv", -1000)]);

        var row = Assert.Single(await db.Store.HistoryAsync(anna, "lives_in"));
        Assert.Equal(Harness.Day(10), row.Fact.RecordedAt);
        Assert.Empty(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(-500), Harness.Day(9)));
        Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(-500), Harness.Day(10)));
    }

    [Fact]
    public async Task Concurrent_writers_on_one_slot_leave_no_overlap_and_the_oracle_agrees()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(40);
        int[] days = [20, 4, 11, 0, 17, 8, 25, 14];

        await Task.WhenAll(days.Select(day => db.IngestAsync($"writer-{day}", Harness.Lives(anna, $"city-{day}", day))));

        var overlapping = (long)(await Scalar(db,
            "select count(*) from facts.facts a join facts.facts b on a.slot = b.slot and a.id < b.id and a.retired_at is null and b.retired_at is null and a.valid && b.valid"))!;
        Assert.Equal(0, overlapping);
        for (var day = 0; day <= 30; day++)
        {
            var expected = days.Where(d => d <= day).Max();
            Assert.Equal($"city-{expected}", Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(day))).Value);
        }
    }

    [Fact]
    public async Task One_namespace_never_sees_anothers_facts()
    {
        await using var db = await postgres.NewAsync("nytka");
        var other = new FactsStore(db.Data, "feed-eater", db.Clock);
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        await db.IngestAsync("first", Harness.Lives(anna, "Kyiv", 0));

        Assert.Single(await db.Store.AsOfAsync(anna, null, Harness.Day(1)));
        Assert.Empty(await other.AsOfAsync(anna, null, Harness.Day(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.IngestAsync(new NewEpisode("test", "x", "x"), [Harness.Lives(anna, "Lviv", 0)]));
    }

    [Fact]
    public async Task Bad_input_is_refused_before_anything_is_written()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        var episode = new NewEpisode("test", "bad", "h");

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.IngestAsync(episode, [new NewFact(anna, "unknown", "x", Harness.Day(0))]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.IngestAsync(episode, [new NewFact(anna, "lives_in", "x", Harness.Day(5), Harness.Day(5))]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.IngestAsync(episode, [new NewFact(anna, "likes", "x", Harness.Day(5), Harness.Day(5))]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.IngestAsync(episode, [new NewFact(anna, "likes", new string('x', 400), Harness.Day(0))]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.DefinePredicateAsync("lives_in", Cardinality.Many));

        Assert.Equal(0, await db.CountAsync("episodes"));
    }

    [Fact]
    public async Task The_schema_applies_through_Switchknife_Migrations_beside_a_consumer_schema()
    {
        var database = postgres.ConnectionStringFor(await postgres.NewDatabaseNameAsync());
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var consumer = new Migrations.DatabaseMigrator(database, typeof(PostgresFixture).Assembly, logger);
        var library = new Migrations.DatabaseMigrator(database, FactsSchema.Scripts, logger);

        var first = (consumer.Run().Count, library.Run().Count);
        var second = (consumer.Run().Count, library.Run().Count);

        await using var data = NpgsqlDataSource.Create(database);
        await using var command = data.CreateCommand("select to_regclass('consumer_notes') is not null and to_regclass('facts.facts') is not null");
        Assert.Equal((1, 1), first);
        Assert.Equal((0, 0), second);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<object?> Scalar(Harness db, string sql)
    {
        await using var command = db.Data.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    private static async Task Execute(Harness db, string sql, params object[] values)
    {
        await using var command = db.Data.CreateCommand(sql);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountLive(Harness db, string predicate) =>
        (long)(await Scalar(db, $"select count(*) from facts.facts where predicate = '{predicate}' and retired_at is null"))!;

    private static async Task<long> CountSources(Harness db, Guid fact) =>
        (long)(await Scalar(db, $"select count(*) from facts.fact_sources where fact_id = '{fact}'"))!;

    private static async Task<long[]> Counts(Harness db) =>
        [await db.CountAsync("entities"), await db.CountAsync("episodes"), await db.CountAsync("facts"), await db.CountAsync("fact_sources")];
}
