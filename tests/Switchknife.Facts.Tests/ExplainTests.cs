using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Switchknife.Facts.Bench;

namespace Switchknife.Facts.Tests;

/// <summary>Spec <c>docs/specs/2026-10-29-facts-explain-fusion.md</c>, E: why a fact is believed, from where, and what became of it.</summary>
public sealed class ExplainTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static async Task<(Harness Db, Guid Anna)> WithAnnaAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        return (db, await db.Store.EnsureEntityAsync("person", "Anna"));
    }

    private static NewFact Likes(Guid subject, string thing) => new(subject, "likes", thing, Harness.Day(0));

    private static NewEpisode Episode(string source, string hash, EpisodeTrust trust = EpisodeTrust.Owner) => new("test", source, hash, Trust: trust);

    private static async Task<Guid> FactIdAsync(Harness db, Guid subject, string predicate, string value) =>
        Assert.Single(await db.Store.HistoryAsync(subject, predicate), v => v.Fact.Value == value && v.RetiredAt is null).Fact.Id;

    private static async Task<long> Count(Harness db, string sql)
    {
        await using var command = db.Data.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Every_extracted_fact_explains_its_sources()
    {
        await using var db = await postgres.NewAsync();
        var clock = new ReplayClock();
        var store = new FactsStore(db.Data, "bench", clock);
        var workload = new Workload(200, 1500, seed: 5, episodeSize: 5);
        foreach (var predicate in Workload.SinglePredicates)
        {
            await store.DefinePredicateAsync(predicate, Cardinality.One);
        }

        await store.DefinePredicateAsync(Workload.ManyPredicate, Cardinality.Many);
        var entities = new Dictionary<Guid, Guid>();
        foreach (var subject in workload.Subjects)
        {
            entities[subject] = await store.EnsureEntityAsync("person", subject.ToString());
        }

        var refs = new HashSet<string>();
        foreach (var episode in workload.Episodes)
        {
            clock.Set(episode.RecordedAt);
            var facts = episode.Singles.Select(s => new NewFact(entities[s.Subject], s.Predicate, s.Value, new DateTimeOffset(s.ValidFrom), LocatorJson: """{"line":1}"""))
                .Concat(episode.Many.Select(m => new NewFact(entities[m.Subject], Workload.ManyPredicate, m.Value, new DateTimeOffset(m.ValidFrom), LocatorJson: """{"line":2}""")))
                .ToArray();
            var reference = string.Create(CultureInfo.InvariantCulture, $"episode-{refs.Count}");
            refs.Add(reference);
            await store.IngestAsync(new NewEpisode("bench", reference, reference), facts);
        }

        var ids = new List<Guid>();
        await using (var command = db.Data.CreateCommand("select id from facts.facts"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        Assert.True(ids.Count > 1000);
        Assert.True(await Count(db, "select count(*) from facts.facts where retired_at is not null") > 0);
        foreach (var id in ids)
        {
            var explanation = await store.ExplainAsync(id);
            Assert.NotNull(explanation);
            Assert.NotEmpty(explanation.Sources);
            Assert.All(explanation.Sources, s =>
            {
                Assert.Equal("bench", s.SourceKind);
                Assert.Contains(s.SourceRef, refs);
                Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(s.LocatorJson).RootElement.ValueKind);
            });
        }
    }

    [Fact]
    public async Task Explain_names_the_episode_locator_and_quote_of_each_source()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(3);
        var occurred = Harness.Day(1);
        await db.Store.IngestAsync(
            new NewEpisode("note", "note-7", "hash-7", occurred),
            [new NewFact(anna, "lives_in", "Kyiv", Harness.Day(0), LocatorJson: """{"line":4}""", Quote: "moved to Kyiv")]);

        var explanation = await db.Store.ExplainAsync(await FactIdAsync(db, anna, "lives_in", "Kyiv"));

        Assert.NotNull(explanation);
        var source = Assert.Single(explanation.Sources);
        Assert.Equal(("note", "note-7", "hash-7", "moved to Kyiv"), (source.SourceKind, source.SourceRef, source.ContentHash, source.Quote));
        Assert.Equal(4, JsonDocument.Parse(source.LocatorJson).RootElement.GetProperty("line").GetInt32());
        Assert.Equal((occurred, Harness.Day(3), EpisodeTrust.Owner), (source.OccurredAt, source.RecordedAt, source.Trust));
        Assert.Equal("Kyiv", explanation.Version.Fact.Value);
    }

    [Fact]
    public async Task Explain_of_a_superseded_fact_names_the_successor_and_the_chain()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [Harness.Lives(anna, "Lviv", 0)]);
        db.At(2);
        await db.Store.IngestAsync(Episode("b", "b"), [Harness.Lives(anna, "Kyiv", 10)]);
        db.At(3);
        await db.Store.IngestAsync(Episode("c", "c"), [Harness.Lives(anna, "Berlin", 20)]);
        var history = await db.Store.HistoryAsync(anna, "lives_in");
        var lviv = history.First(v => v.Fact.Value == "Lviv" && v.RetiredReason == "superseded");
        var kyiv = history.First(v => v.Fact.Value == "Kyiv" && v.RetiredReason == "superseded");
        var berlin = history.Single(v => v.Fact.Value == "Berlin");

        var ofLviv = await db.Store.ExplainAsync(lviv.Fact.Id);

        Assert.NotNull(ofLviv);
        Assert.Equal("superseded", ofLviv.Version.RetiredReason);
        Assert.Equal(["Kyiv", "Berlin"], ofLviv.Successors.Select(v => v.Fact.Value));
        Assert.Equal([kyiv.Fact.Id, berlin.Fact.Id], ofLviv.Successors.Select(v => v.Fact.Id));
        Assert.Equal([lviv.Fact.Id], (await db.Store.ExplainAsync(kyiv.Fact.Id))!.Predecessors.Select(v => v.Fact.Id));
        Assert.Empty((await db.Store.ExplainAsync(berlin.Fact.Id))!.Successors);
        var narrowed = history.First(v => v.Fact.Value == "Lviv" && v.Fact.Id != lviv.Fact.Id);
        var ofNarrowed = await db.Store.ExplainAsync(narrowed.Fact.Id);
        Assert.NotNull(ofNarrowed);
        Assert.Equal("a", Assert.Single(ofNarrowed.Sources).SourceRef);
        Assert.Empty(ofNarrowed.Successors);
    }

    [Fact]
    public async Task Explain_lists_every_corroborating_source_and_the_highest_trust()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("stranger", "s", EpisodeTrust.ThirdParty), [Likes(anna, "jazz"), Likes(anna, "tea")]);
        db.At(2);
        await db.Store.IngestAsync(Episode("mine", "m"), [Likes(anna, "jazz")]);

        var jazz = await db.Store.ExplainAsync(await FactIdAsync(db, anna, "likes", "jazz"));
        var tea = await db.Store.ExplainAsync(await FactIdAsync(db, anna, "likes", "tea"));

        Assert.NotNull(jazz);
        Assert.NotNull(tea);
        Assert.Equal(["stranger", "mine"], jazz.Sources.Select(s => s.SourceRef));
        Assert.Equal(EpisodeTrust.Owner, jazz.Trust);
        Assert.Equal(EpisodeTrust.ThirdParty, tea.Trust);
    }

    [Fact]
    public async Task Explain_reports_the_retraction_state()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [Likes(anna, "jazz"), Likes(anna, "tea")]);
        var jazz = await FactIdAsync(db, anna, "likes", "jazz");
        Assert.Null((await db.Store.ExplainAsync(jazz))!.Retraction);

        db.At(2);
        await db.Store.RetractAsync(jazz);
        var retracted = await db.Store.ExplainAsync(jazz);
        Assert.NotNull(retracted);
        Assert.Equal("retracted", retracted.Version.RetiredReason);
        Assert.Equal(new FactRetraction(Harness.Day(2), null), retracted.Retraction);

        db.At(3);
        await db.Store.RestoreAsync(jazz);
        var restored = await db.Store.ExplainAsync(await FactIdAsync(db, anna, "likes", "jazz"));
        Assert.NotNull(restored);
        Assert.Null(restored.Version.RetiredAt);
        Assert.Equal(new FactRetraction(Harness.Day(2), Harness.Day(3)), restored.Retraction);
    }

    [Fact]
    public async Task Explain_of_an_outranked_fact_says_so_and_has_no_successor()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("mine", "a"), [Harness.Lives(anna, "Lviv", 0)]);
        db.At(2);
        await db.Store.IngestAsync(Episode("stranger", "b", EpisodeTrust.ThirdParty), [Harness.Lives(anna, "Kyiv", 10)]);
        var kyiv = Assert.Single(await db.Store.HistoryAsync(anna, "lives_in"), v => v.Fact.Value == "Kyiv");

        var explanation = await db.Store.ExplainAsync(kyiv.Fact.Id);

        Assert.NotNull(explanation);
        Assert.Equal("outranked", explanation.Version.RetiredReason);
        Assert.Empty(explanation.Successors);
        Assert.Equal(EpisodeTrust.ThirdParty, explanation.Trust);
    }

    [Fact]
    public async Task Explain_of_a_fact_retired_by_a_changed_source_still_shows_the_old_version_as_a_source()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("S", "h1"), [Likes(anna, "tea"), Likes(anna, "jazz")]);
        db.At(2);
        await db.Store.IngestAsync(Episode("S", "h2"), [Likes(anna, "tea")]);
        var jazz = Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Value == "jazz");

        var explanation = await db.Store.ExplainAsync(jazz.Fact.Id);

        Assert.NotNull(explanation);
        Assert.Equal("source_changed", explanation.Version.RetiredReason);
        Assert.Equal("h1", Assert.Single(explanation.Sources).ContentHash);
    }

    [Fact]
    public async Task Explain_returns_null_for_an_unknown_foreign_or_purged_fact()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("s", "s"), [Likes(anna, "opera"), Likes(anna, "tea")]);
        db.At(2);
        await db.Store.IngestAsync(Episode("t", "t"), [Likes(anna, "tea")]);
        var opera = await FactIdAsync(db, anna, "likes", "opera");
        var tea = await FactIdAsync(db, anna, "likes", "tea");
        var other = new FactsStore(db.Data, "other", db.Clock);
        await other.DefinePredicateAsync("likes", Cardinality.Many);
        var bob = await other.EnsureEntityAsync("person", "Bob");
        await other.IngestAsync(Episode("x", "x"), [Likes(bob, "chess")]);
        var chess = (await other.AsOfAsync(bob, "likes", Harness.Day(1))).Single().Id;

        Assert.Null(await db.Store.ExplainAsync(Guid.NewGuid()));
        Assert.Null(await db.Store.ExplainAsync(chess));
        Assert.NotNull(await other.ExplainAsync(chess));

        await db.Store.PurgeSourceAsync("test", "s");

        Assert.Null(await db.Store.ExplainAsync(opera));
        Assert.Equal("t", Assert.Single((await db.Store.ExplainAsync(tea))!.Sources).SourceRef);
    }

    [Fact]
    public async Task Explain_costs_one_round_trip()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [Harness.Lives(anna, "Lviv", 0), Likes(anna, "jazz")]);
        db.At(2);
        await db.Store.IngestAsync(Episode("b", "b"), [Harness.Lives(anna, "Kyiv", 10), Likes(anna, "jazz")]);
        db.At(3);
        await db.Store.IngestAsync(Episode("c", "c"), [Harness.Lives(anna, "Berlin", 20), Likes(anna, "jazz")]);
        db.At(4);
        await db.Store.IngestAsync(Episode("d", "d"), [Harness.Lives(anna, "Paris", 30)]);
        var lviv = Assert.Single(await db.Store.HistoryAsync(anna, "lives_in"), v => v.Fact.Value == "Lviv" && v.RetiredReason == "superseded");
        var jazz = await FactIdAsync(db, anna, "likes", "jazz");

        FactExplanation? chain = null;
        FactExplanation? sources = null;
        var chainTrips = await RoundTripsAsync(db, async () => chain = await db.Store.ExplainAsync(lviv.Fact.Id));
        var sourceTrips = await RoundTripsAsync(db, async () => sources = await db.Store.ExplainAsync(jazz));

        Assert.Equal(3, chain!.Successors.Count);
        Assert.Equal(3, sources!.Sources.Count);
        Assert.Equal((1, 1), (chainTrips, sourceTrips));
    }

    [Fact]
    public async Task Explain_of_a_source_less_fact_inserted_by_raw_sql_returns_an_empty_source_list()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [Likes(anna, "jazz")]);
        var id = Guid.NewGuid();
        await using (var insert = db.Data.CreateCommand(
            """
            insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, fingerprint, episode_id)
            select $1, namespace, subject_id, predicate, 'rain', 'likes rain', valid, recorded_at, 'raw', episode_id from facts.facts limit 1
            """))
        {
            insert.Parameters.AddWithValue(id);
            await insert.ExecuteNonQueryAsync();
        }

        var explanation = await db.Store.ExplainAsync(id);

        Assert.NotNull(explanation);
        Assert.Empty(explanation.Sources);
        Assert.Equal(EpisodeTrust.ThirdParty, explanation.Trust);
    }

    [Fact]
    public async Task Successor_chains_stop_at_a_cycle_and_at_32_steps()
    {
        var (db, anna) = await WithAnnaAsync(postgres);
        await using var _ = db;
        db.At(1);
        await db.Store.IngestAsync(Episode("a", "a"), [.. Enumerable.Range(0, 42).Select(i => Likes(anna, $"thing {i:00}"))]);
        var ids = new List<Guid>();
        await using (var list = db.Data.CreateCommand("select id from facts.facts order by object_value"))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await using (var batch = db.Data.CreateBatch())
        {
            // Rows 0..39 form a chain; rows 40 and 41 point at each other.
            for (var i = 0; i < 39; i++)
            {
                batch.BatchCommands.Add(Pointer(ids[i], ids[i + 1]));
            }

            batch.BatchCommands.Add(Pointer(ids[40], ids[41]));
            batch.BatchCommands.Add(Pointer(ids[41], ids[40]));
            await batch.ExecuteNonQueryAsync();
        }

        var chain = await db.Store.ExplainAsync(ids[0]);
        var cycle = await db.Store.ExplainAsync(ids[40]);

        Assert.Equal(32, chain!.Successors.Count);
        Assert.Equal(ids.Skip(1).Take(32), chain.Successors.Select(v => v.Fact.Id));
        Assert.Equal([ids[41]], cycle!.Successors.Select(v => v.Fact.Id));
    }

    private static NpgsqlBatchCommand Pointer(Guid from, Guid to)
    {
        var command = new NpgsqlBatchCommand("update facts.facts set superseded_by = $2 where id = $1");
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);
        return command;
    }

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
}
