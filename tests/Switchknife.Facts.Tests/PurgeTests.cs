using Npgsql;

namespace Switchknife.Facts.Tests;

/// <summary>Spec <c>docs/specs/2026-10-28-facts-write-path.md</c>, W2: purge by slot replay and the replay as an integrity check.</summary>
public sealed class PurgeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string StateSql =
        """
        select concat_ws('|', s.name, f.predicate, coalesce(f.object_value, o.name), f.text, lower(f.valid)::text, upper(f.valid)::text, f.recorded_at::text,
            f.retired_at::text, f.retired_reason, f.superseded_by is not null,
            (select source_ref from facts.episodes where id = f.episode_id),
            (select string_agg(ep.source_ref || ':' || x.locator::text, ',' order by ep.source_ref, x.locator::text)
             from facts.fact_sources x join facts.episodes ep on ep.id = x.episode_id where x.fact_id = f.id))
        from facts.facts f join facts.entities s on s.id = f.subject_id left join facts.entities o on o.id = f.object_id
        union all
        select concat_ws('|', 'episode', source_kind, source_ref, content_hash, recorded_at::text, trust) from facts.episodes
        union all
        select concat_ws('|', 'retraction', retracted_at::text, restored_at::text) from facts.retractions
        """;

    private static async Task<Harness> NewAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        await db.Store.DefinePredicateAsync("works_at", Cardinality.One);
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        return db;
    }

    private static NewEpisode Episode(string source, string hash = "h", EpisodeTrust trust = EpisodeTrust.Owner) => new("test", source, hash, Trust: trust);

    private static NewFact Likes(Guid subject, string thing) => new(subject, "likes", thing, Harness.Day(0), LocatorJson: $$"""{"seg":"{{thing}}"}""");

    private static NewFact Lives(Guid subject, string city, int from, int? to = null) =>
        new(subject, "lives_in", city, Harness.Day(from), to is { } t ? Harness.Day(t) : null, LocatorJson: $$"""{"seg":"{{city}}"}""");

    /// <summary>The same history for every store, ids aside; <paramref name="skip"/> leaves one source out, as if it never existed.</summary>
    private static async Task RunHistoryAsync(Harness db, string? skip)
    {
        var anna = await db.Store.EnsureEntityAsync("person", "Anna", "app:anna");
        var boris = await db.Store.EnsureEntityAsync("person", "Boris", "app:boris");
        var acme = await db.Store.EnsureEntityAsync("org", "Acme", "app:acme");

        async Task Ingest(int day, string source, string hash, EpisodeTrust trust, params NewFact[] facts)
        {
            db.At(day);
            if (source != skip)
            {
                await db.Store.IngestAsync(Episode(source, hash, trust), facts);
            }
        }

        await Ingest(1, "e1", "h", EpisodeTrust.Owner, Lives(anna, "Kyiv", 0), Likes(anna, "tea"), Lives(boris, "Oslo", 0));
        await Ingest(2, "e2", "h", EpisodeTrust.Owner, Lives(anna, "Lviv", 10), new NewFact(boris, "works_at", null, Harness.Day(0), Text: "Boris works at Acme", ObjectEntity: acme));
        await Ingest(3, "e3", "h", EpisodeTrust.Owner, Lives(anna, "Paris", 20), Likes(anna, "jazz"), Lives(boris, "Rome", 5));
        await Ingest(4, "e4", "h", EpisodeTrust.ThirdParty, Lives(anna, "Berlin", 30), Lives(boris, "Paris", 6));
        await Ingest(5, "e5", "h", EpisodeTrust.Owner, Lives(anna, "Oslo", 2, 4), Lives(anna, "Rome", 22, 25));
        db.At(6);
        var tea = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(6)), f => f.Value == "tea");
        await db.Store.RetractAsync(tea.Id);
        await Ingest(7, "s", "h1", EpisodeTrust.Owner, Likes(anna, "opera"), Lives(boris, "Madrid", 40));
        await Ingest(8, "s", "h2", EpisodeTrust.Owner, Likes(anna, "opera"));
        await Ingest(9, "e6", "h", EpisodeTrust.Owner, Likes(anna, "jazz"), Lives(anna, "Lviv", 10), Likes(anna, "tea"));
    }

    private static async Task<string[]> StateAsync(Harness db)
    {
        var lines = new List<string>();
        await using var command = db.Data.CreateCommand(StateSql);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Purging_a_source_rebuilds_the_slot_as_if_it_never_existed()
    {
        await using var purged = await NewAsync(postgres);
        await using var never = await NewAsync(postgres);
        await RunHistoryAsync(purged, skip: null);
        await RunHistoryAsync(never, skip: "e3");
        Assert.NotEqual(await StateAsync(never), await StateAsync(purged));

        var result = await purged.Store.PurgeSourceAsync("test", "e3");

        Assert.Equal((1, 2, 2), (result.Episodes, result.Assertions, result.SlotsRebuilt));
        var expected = await StateAsync(never);
        var actual = await StateAsync(purged);
        Assert.Empty(expected.Except(actual).Select(l => "missing: " + l).Concat(actual.Except(expected).Select(l => "extra: " + l)));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Replaying_an_untouched_slot_reproduces_the_stored_rows_exactly()
    {
        await using var db = await NewAsync(postgres);
        await RunHistoryAsync(db, skip: null);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna", "app:anna");
        var boris = await db.Store.EnsureEntityAsync("person", "Boris", "app:boris");
        Assert.Empty(await db.Store.VerifySlotAsync(anna, "lives_in"));
        Assert.Empty(await db.Store.VerifySlotAsync(boris, "lives_in"));
        Assert.Empty(await db.Store.VerifySlotAsync(boris, "works_at"));
        var before = await StateAsync(db);
        const string ids = "select md5(string_agg(id::text, ',' order by id)) from facts.facts";
        var idsBefore = await Scalar(db, ids);

        // A raw edit of one retired row is named.
        await using (var edit = db.Data.CreateCommand("update facts.facts set retired_at = retired_at + interval '1 day' where id = (select id from facts.facts where retired_reason = 'superseded' limit 1)"))
        {
            await edit.ExecuteNonQueryAsync();
        }

        var differences = await db.Store.VerifySlotAsync(anna, "lives_in");
        Assert.Contains(differences, d => d.StartsWith("stored: ", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.StartsWith("replayed: ", StringComparison.Ordinal));
        Assert.NotEqual(before, await StateAsync(db));
        Assert.Equal(idsBefore, await Scalar(db, ids));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(23)]
    public async Task Replaying_a_random_history_of_many_slots_reproduces_the_stored_rows(int seed)
    {
        await using var db = await NewAsync(postgres);
        db.Clock.AutoAdvanceAmount = TimeSpan.FromMinutes(1);
        var random = new Random(seed);
        var people = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            people.Add(await db.Store.EnsureEntityAsync("person", $"P{i}"));
        }

        var live = new List<Guid>();
        for (var i = 0; i < 300; i++)
        {
            var person = people[random.Next(people.Count)];
            var from = random.Next(0, 60);
            int? to = random.Next(4) == 0 ? from + random.Next(1, 20) : null;
            var trust = (EpisodeTrust)random.Next(3);
            var source = $"src{random.Next(40)}";
            var episode = new NewEpisode("test", source, $"v{i}", Trust: trust);
            await db.Store.IngestAsync(episode, [Lives(person, $"city {random.Next(6)}", from, to)]);
            if (random.Next(10) == 0)
            {
                var asOf = await db.Store.AsOfAsync(person, "lives_in", Harness.Day(from));
                if (asOf.Count == 1)
                {
                    await db.Store.RetractAsync(asOf[0].Id);
                    live.Add(asOf[0].Id);
                }
            }

            if (live.Count > 0 && random.Next(15) == 0)
            {
                try
                {
                    await db.Store.RestoreAsync(live[random.Next(live.Count)]);
                }
                catch (InvalidOperationException)
                {
                    // agent-checks: allow empty-catch -- a random restore may meet a newer fact on its interval; that refusal is the behaviour under test
                }
            }
        }

        foreach (var person in people)
        {
            var differences = await db.Store.VerifySlotAsync(person, "lives_in");
            string dump = "";
            if (differences.Count > 0)
            {
                await using var all = db.Data.CreateCommand($"select concat_ws(' | ', object_value, lower(valid)::text, upper(valid)::text, recorded_at::text, retired_at::text, retired_reason, (select source_ref||'/'||content_hash from facts.episodes where id = episode_id), (select string_agg(ep.source_ref||'/'||ep.content_hash, ',') from facts.fact_sources x join facts.episodes ep on ep.id = x.episode_id where x.fact_id = f.id)) from facts.facts f where subject_id = '{person}' order by recorded_at, lower(valid)");
                await using var reader = await all.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    dump += "\n  " + reader.GetString(0);
                }
            }

            Assert.True(differences.Count == 0, string.Join('\n', differences) + dump);
        }
    }

    [Fact]
    public async Task Purge_deletes_what_only_the_source_supports_and_keeps_the_rest()
    {
        await using var db = await NewAsync(postgres);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        db.At(1);
        await db.Store.IngestAsync(Episode("s"), [Likes(anna, "tea"), Likes(anna, "opera")]);
        db.At(2);
        await db.Store.IngestAsync(Episode("t"), [Likes(anna, "tea")]);
        var facts = await db.Store.AsOfAsync(anna, "likes", Harness.Day(3));
        await db.Store.SetEmbeddingsAsync("m", [.. facts.Select((f, i) => new FactEmbedding(f.Id, new float[] { 1, i, 0 }))]);
        Assert.Equal(2, await Count(db, "select count(*) from facts.fact_vectors"));

        var result = await db.Store.PurgeSourceAsync("test", "s");

        Assert.Equal((1, 1, 0), (result.Episodes, result.Assertions, result.SlotsRebuilt));
        var tea = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(3)));
        Assert.Equal("tea", tea.Value);
        Assert.Equal(1, await Count(db, $"select count(*) from facts.fact_sources where fact_id = '{tea.Id}'"));
        Assert.Equal(1, await Count(db, $"select count(*) from facts.facts f join facts.episodes e on e.id = f.episode_id where f.id = '{tea.Id}' and e.source_ref = 't'"));
        Assert.Equal(1, await Count(db, "select count(*) from facts.facts"));
        Assert.Equal((1, 1, 1), (await Count(db, "select count(*) from facts.fact_vectors"), await Count(db, "select count(*) from facts.fact_embeddings"), await Count(db, "select count(*) from facts.episodes")));
        Assert.Equal(new PurgeResult(0, 0, 0), await db.Store.PurgeSourceAsync("test", "s"));
    }

    [Fact]
    public async Task Purge_touches_nothing_outside_the_affected_slots()
    {
        await using var db = await NewAsync(postgres);
        await RunHistoryAsync(db, skip: null);
        const string hash = "select md5(coalesce(string_agg(f::text, ',' order by id), '')) from facts.facts f where subject_id = (select id from facts.entities where external_ref = 'app:boris') and predicate = 'works_at'";
        var before = await Scalar(db, hash);
        var annaBefore = await Scalar(db, "select md5(coalesce(string_agg(f::text, ',' order by id), '')) from facts.facts f where predicate = 'likes' and object_value = 'opera'");

        await db.Store.PurgeSourceAsync("test", "e3");

        Assert.Equal(before, await Scalar(db, hash));
        Assert.Equal(annaBefore, await Scalar(db, "select md5(coalesce(string_agg(f::text, ',' order by id), '')) from facts.facts f where predicate = 'likes' and object_value = 'opera'"));
    }

    [Fact]
    public async Task Purge_keeps_embeddings_of_rebuilt_rows()
    {
        await using var db = await NewAsync(postgres);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        db.At(1);
        await db.Store.IngestAsync(Episode("a"), [Lives(anna, "Kyiv", 0)]);
        db.At(2);
        await db.Store.IngestAsync(Episode("b"), [Lives(anna, "Lviv", 10)]);
        db.At(3);
        await db.Store.IngestAsync(Episode("c"), [Lives(anna, "Paris", 20)]);
        var history = await db.Store.HistoryAsync(anna, "lives_in");
        float[] Vector(string city) => city switch { "Kyiv" => [1, 0, 0], "Lviv" => [0, 1, 0], _ => [0, 0, 1] };
        await db.Store.SetEmbeddingsAsync("m", [.. history.GroupBy(v => v.Fact.Value).Select(g => new FactEmbedding(g.First().Fact.Id, Vector(g.Key!)))]);

        await db.Store.PurgeSourceAsync("test", "c");

        var kyiv = Assert.Single(await db.Store.NearestAsync("m", Vector("Kyiv"), 1, Harness.Day(5)));
        var lviv = Assert.Single(await db.Store.NearestAsync("m", Vector("Lviv"), 1, Harness.Day(25)));
        Assert.Equal(("Kyiv", "Lviv"), (kyiv.Fact.Value, lviv.Fact.Value));
        Assert.Equal(1.0, lviv.Similarity, 3);
        Assert.Empty(await db.Store.VerifySlotAsync(anna, "lives_in"));
    }

    [Fact]
    public async Task A_failed_purge_changes_nothing()
    {
        await using var db = await NewAsync(postgres);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        db.At(1);
        await db.Store.IngestAsync(Episode("a"), [new NewFact(anna, "lives_in", "Kyiv", Harness.Day(0), Text: "poison")]);
        db.At(2);
        await db.Store.IngestAsync(Episode("c"), [Lives(anna, "Lviv", 10), Likes(anna, "tea")]);
        var before = await StateAsync(db);
        var counts = new[] { await db.CountAsync("facts"), await db.CountAsync("fact_sources"), await db.CountAsync("episodes") };
        await using (var trigger = db.Data.CreateCommand(
            """
            create function facts.boom() returns trigger language plpgsql as $$ begin if new.text = 'poison' then raise exception 'boom'; end if; return new; end $$;
            create trigger boom before insert on facts.facts for each row execute function facts.boom();
            """))
        {
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<PostgresException>(() => db.Store.PurgeSourceAsync("test", "c"));

        await using (var drop = db.Data.CreateCommand("drop trigger boom on facts.facts"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        Assert.Equal(before, await StateAsync(db));
        Assert.Equal(counts, new[] { await db.CountAsync("facts"), await db.CountAsync("fact_sources"), await db.CountAsync("episodes") });
    }

    [Fact]
    public async Task Purge_removes_the_retraction_of_an_assertion_that_vanishes()
    {
        await using var db = await NewAsync(postgres);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        db.At(1);
        await db.Store.IngestAsync(Episode("s"), [Likes(anna, "opera")]);
        await db.Store.IngestAsync(Episode("t"), [Likes(anna, "tea")]);
        var opera = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(2)), f => f.Value == "opera");
        var tea = Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(2)), f => f.Value == "tea");
        db.At(2);
        await db.Store.RetractAsync(opera.Id);
        await db.Store.RetractAsync(tea.Id);
        Assert.Equal(2, await db.CountAsync("retractions"));

        await db.Store.PurgeSourceAsync("test", "s");

        Assert.Equal(1, await db.CountAsync("retractions"));
        db.At(3);
        var again = await db.Store.IngestAsync(Episode("u"), [Likes(anna, "tea"), Likes(anna, "opera")]);
        Assert.Equal((1, 0), (again.Written, again.Corroborated));
    }

    [Fact]
    public async Task Purge_is_atomic_with_concurrent_writers_on_the_slot()
    {
        await using var db = await NewAsync(postgres);
        db.Clock.AutoAdvanceAmount = TimeSpan.FromSeconds(1);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        await db.Store.IngestAsync(Episode("a"), [Lives(anna, "Kyiv", 0)]);
        await db.Store.IngestAsync(Episode("c"), [Lives(anna, "Lviv", 10)]);

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                await db.Store.IngestAsync(Episode($"w{w}", $"v{i}"), [Lives(anna, $"city {w}-{i}", (w * 8) + i, i % 3 == 0 ? (w * 8) + i + 5 : null)]);
            }
        })).ToArray();
        var purge = Task.Run(async () =>
        {
            await Task.Yield();
            return await db.Store.PurgeSourceAsync("test", "c");
        });
        await Task.WhenAll([.. writers, purge]);

        Assert.Equal(1, (await purge).Episodes);
        Assert.Equal(0, await Count(db, "select count(*) from facts.episodes where source_ref = 'c'"));
        Assert.Equal(0, await Count(db, "select count(*) from facts.facts a join facts.facts b on a.slot = b.slot and a.id < b.id and a.valid && b.valid where a.retired_at is null and b.retired_at is null"));
        Assert.Empty(await db.Store.VerifySlotAsync(anna, "lives_in"));
    }

    private static async Task<object?> Scalar(Harness db, string sql)
    {
        await using var command = db.Data.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<long> Count(Harness db, string sql) => (long)(await Scalar(db, sql))!;
}
