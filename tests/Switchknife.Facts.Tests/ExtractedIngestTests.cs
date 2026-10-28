using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Tests;

/// <summary>Spec <c>docs/specs/2026-10-30-facts-extraction-contract.md</c>, I: validated extractor output into the store.</summary>
public sealed class ExtractedIngestTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly NewEpisode Conversation = new("conversation", "c-1", "h1");

    private static ExtractedFact Likes(string thing, string? replaces = null) =>
        new(new ExtractedMention("e1", null, null), "likes", thing, null, $"Anna likes {thing}.", null, null, replaces, ["l2"], null, 0.5);

    private static ExtractedFact LivesInKyiv =>
        new(new ExtractedMention("e1", null, null), "lives_in", "Kyiv", null, "Anna lives in Kyiv.", "2026-03-01", null, null, ["l1"], "moved to Kyiv", 0.9);

    private static string Reply(params ExtractedFact[] facts) => JsonSerializer.Serialize(new ExtractedEpisode(facts), ExtractionJsonContext.Default.ExtractedEpisode);

    private async Task<(Harness Db, Guid Anna, Guid Bob)> NewAsync()
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        db.At(2);
        return (db, await db.Store.EnsureEntityAsync("person", "Anna"), await db.Store.EnsureEntityAsync("person", "Bob"));
    }

    private static ExtractionContext Context(Guid anna, Guid bob, params KnownFact[] live) => new(
        Harness.Day(10),
        [new EvidenceLine("l1", "I moved to Kyiv last March.", "Anna"), new EvidenceLine("l2", "I like tea, coffee and jazz.", "Anna")],
        [new PredicateSpec("lives_in", Cardinality.One), new PredicateSpec("likes", Cardinality.Many)],
        [new KnownEntity(anna, "person", "Anna"), new KnownEntity(bob, "person", "Bob")],
        live);

    private static ExtractedMention Named(string name) => new(null, name, "person");

    /// <summary>The ingest clock moves to day 10, the episode time of <see cref="Context"/>, unless it is past it.</summary>
    private static void Now(Harness db)
    {
        if (db.Clock.GetUtcNow() < Harness.Day(10))
        {
            db.At(10);
        }
    }

    private static Task<ExtractionOutcome> Extract(Harness db, IFactExtractor extractor, NewEpisode episode, ExtractionContext context, CancellationToken cancellationToken = default)
    {
        Now(db);
        return db.Store.ExtractAsync(extractor, episode, context, cancellationToken: cancellationToken);
    }

    /// <summary>Ingests a fact the plain way, at the current ingest time (day 2 unless a test moved it).</summary>
    private static async Task<Guid> PlainLikeAsync(Harness db, Guid subject, string thing, string source, EpisodeTrust trust = EpisodeTrust.Owner)
    {
        await db.Store.IngestAsync(new NewEpisode("conversation", source, source, Trust: trust), [new NewFact(subject, "likes", thing, Harness.Day(1), Text: $"likes {thing}")]);
        return Assert.Single(await db.Store.HistoryAsync(subject, "likes"), v => v.Fact.Value == thing).Fact.Id;
    }

    private static async Task<T> Scalar<T>(Harness db, string sql)
    {
        await using var command = db.Data.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static readonly string[] s_tables = ["entities", "episodes", "facts", "fact_sources", "retractions"];

    private static async Task<long[]> Counts(Harness db) => [.. await Task.WhenAll(s_tables.Select(t => db.CountAsync(t)))];

    [Fact]
    public async Task Ingesting_the_same_episode_twice_changes_nothing()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var extractor = new ScriptedExtractor(Reply(LivesInKyiv, Likes("jazz")));

        var first = await Extract(db, extractor, Conversation, Context(anna, bob));
        var before = await Counts(db);
        var second = await Extract(db, extractor, Conversation, Context(anna, bob));

        Assert.Equal(ExtractionStatus.Ingested, first.Status);
        Assert.Equal(2, first.Ingest!.Ingest.Written);
        Assert.Equal(ExtractionStatus.Duplicate, second.Status);
        Assert.Null(second.Ingest);
        Assert.Equal(1, extractor.Calls);
        Assert.Equal(before, await Counts(db));

        // The store-level ingest of validated output is idempotent too.
        var direct = await db.Store.IngestExtractedAsync(Conversation, first.Validation!, new ExtractorInfo("x", "m"), new ExactNameEntityResolver(db.Store));
        Assert.True(direct.Ingest.Duplicate);
        Assert.Equal(before, await Counts(db));
    }

    [Fact]
    public async Task A_failing_extractor_leaves_no_partial_facts()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var extractor = new ScriptedExtractor(new InvalidOperationException("timeout while sending Anna's passcode 4417"), Reply(LivesInKyiv, Likes("jazz")));

        var failed = await Extract(db, extractor, Conversation, Context(anna, bob));

        Assert.Equal(ExtractionStatus.Failed, failed.Status);
        Assert.Equal(ExtractionFailures.ExtractorError, failed.FailureCode);
        Assert.IsType<InvalidOperationException>(failed.Error);
        Assert.Equal(EpisodeStatus.Failed, await db.Store.EpisodeStatusAsync("conversation", "c-1", "h1"));
        Assert.Equal(("failed", "extractor_error"), (await Scalar<string>(db, "select status from facts.episodes"), await Scalar<string>(db, "select failure from facts.episodes")));
        Assert.Equal((0L, 0L), (await db.CountAsync("facts"), await db.CountAsync("fact_sources")));

        var retried = await Extract(db, extractor, Conversation, Context(anna, bob));

        Assert.Equal(ExtractionStatus.Ingested, retried.Status);
        Assert.Equal(1L, await db.CountAsync("episodes"));
        Assert.Equal(("done", true), (await Scalar<string>(db, "select status from facts.episodes"), await Scalar<bool>(db, "select failure is null from facts.episodes")));
        Assert.Equal(2L, await db.CountAsync("facts"));
        Assert.Equal(EpisodeStatus.Done, await db.Store.EpisodeStatusAsync("conversation", "c-1", "h1"));
    }

    [Fact]
    public async Task A_replaces_id_that_is_not_a_live_fact_of_the_subject_is_dropped()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var coffee = await PlainLikeAsync(db, bob, "coffee", "seed");
        var live = new KnownFact(coffee, bob, "likes", "Bob likes coffee.");
        var extractor = new ScriptedExtractor(Reply(Likes("tea", replaces: "f1")), Reply(Likes("jazz", replaces: "f99")));

        var other = await Extract(db, extractor, Conversation, Context(anna, bob, live));
        var unknown = await Extract(db, extractor, new NewEpisode("conversation", "c-2", "h2"), Context(anna, bob, live));

        Assert.Equal((1, 0, 1), (other.Ingest!.Ingest.Written, other.Ingest.Replaced, other.Validation!.ReplacesDropped));
        Assert.Equal((1, 0, 1), (unknown.Ingest!.Ingest.Written, unknown.Ingest.Replaced, unknown.Validation!.ReplacesDropped));
        Assert.Equal("coffee", Assert.Single(await db.Store.AsOfAsync(bob, "likes", Harness.Day(11))).Value);
        var bobs = Assert.Single(await db.Store.HistoryAsync(bob, "likes"));
        Assert.Null(bobs.RetiredAt);
        Assert.Equal(2, (await db.Store.HistoryAsync(anna, "likes")).Count);
    }

    [Fact]
    public async Task Output_that_does_not_parse_is_a_failed_episode_with_a_code()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var extractor = new ScriptedExtractor("I'm sorry, I can't help with that.", "{}", "", "{\"facts\":[{\"evidence\":\"l1\"}]}");

        var codes = new List<string?>();
        for (var i = 0; i < 4; i++)
        {
            var outcome = await Extract(db, extractor, new NewEpisode("conversation", $"c-{i}", "h"), Context(anna, bob));
            Assert.Equal(ExtractionStatus.Failed, outcome.Status);
            Assert.Null(outcome.Error);
            codes.Add(outcome.FailureCode);
        }

        Assert.Equal([ExtractionFailures.BadJson, ExtractionFailures.BadShape, ExtractionFailures.BadJson, ExtractionFailures.BadShape], codes);
        Assert.Equal((0L, 4L), (await db.CountAsync("facts"), await db.CountAsync("episodes")));
        Assert.Equal(4L, await Scalar<long>(db, "select count(*) from facts.episodes where status = 'failed'"));
    }

    [Fact]
    public async Task A_failed_new_version_keeps_the_earlier_versions_facts()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var extractor = new ScriptedExtractor(Reply(Likes("jazz")), new TimeoutException(), Reply(Likes("tea")));

        await Extract(db, extractor, Conversation, Context(anna, bob));
        db.At(11);
        var failed = await Extract(db, extractor, Conversation with { ContentHash = "h2" }, Context(anna, bob));
        Assert.Equal(ExtractionStatus.Failed, failed.Status);
        var during = await db.Store.AsOfAsync(anna, "likes", Harness.Day(12));
        Assert.Equal("jazz", Assert.Single(during).Value);

        db.At(12);
        var retried = await Extract(db, extractor, Conversation with { ContentHash = "h2" }, Context(anna, bob));
        Assert.Equal(ExtractionStatus.Ingested, retried.Status);

        var history = await db.Store.HistoryAsync(anna, "likes");
        Assert.Equal("source_changed", Assert.Single(history, v => v.Fact.Value == "jazz").RetiredReason);
        Assert.Null(Assert.Single(history, v => v.Fact.Value == "tea").RetiredAt);
        Assert.Equal("tea", Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(13))).Value);
        Assert.Equal("jazz", Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(13), Harness.Day(11))).Value);
    }

    [Fact]
    public async Task Extractor_model_and_tokens_are_stored_and_explain_reports_them()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var plain = await PlainLikeAsync(db, anna, "tea", "plain");
        var named = new ScriptedExtractor(new ExtractorResponse(Reply(Likes("jazz")), new ExtractorInfo("my-extractor", "gpt-test", 120, 30)));
        var unnamed = new ScriptedExtractor(new ExtractorResponse(Reply(Likes("coffee")), new ExtractorInfo(null, "gpt-test")));

        await Extract(db, named, Conversation, Context(anna, bob));
        await Extract(db, unnamed, new NewEpisode("conversation", "c-2", "h2"), Context(anna, bob));

        Assert.Equal(("my-extractor", "gpt-test", 120, 30), (
            await Scalar<string>(db, "select extractor from facts.episodes where source_ref = 'c-1'"), await Scalar<string>(db, "select model from facts.episodes where source_ref = 'c-1'"),
            await Scalar<int>(db, "select input_tokens from facts.episodes where source_ref = 'c-1'"), await Scalar<int>(db, "select output_tokens from facts.episodes where source_ref = 'c-1'")));

        var history = await db.Store.HistoryAsync(anna, "likes");
        var jazz = await db.Store.ExplainAsync(history.Single(v => v.Fact.Value == "jazz").Fact.Id);
        var source = Assert.Single(jazz!.Sources);
        Assert.Equal(("my-extractor", "gpt-test"), (source.Extractor, source.Model));

        var coffee = await db.Store.ExplainAsync(history.Single(v => v.Fact.Value == "coffee").Fact.Id);
        Assert.StartsWith("kit/1+", Assert.Single(coffee!.Sources).Extractor, StringComparison.Ordinal);
        Assert.Equal(ExtractionPrompt.Build(Context(anna, bob)).ExtractorName, Assert.Single(coffee.Sources).Extractor);

        var untouched = await db.Store.ExplainAsync(plain);
        Assert.Null(Assert.Single(untouched!.Sources).Extractor);
        Assert.Null(Assert.Single(untouched.Sources).Model);
    }

    [Fact]
    public async Task Accepted_facts_are_written_with_locator_quote_and_resolved_entities()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var carol = Likes("jazz") with { Subject = Named("  CAROL ") };
        var extractor = new ScriptedExtractor(Reply(LivesInKyiv with { Evidence = ["l1", "l2"] }, carol, carol with { Value = "tea", Text = "Carol likes tea." }));

        var outcome = await Extract(db, extractor, Conversation, Context(anna, bob));

        Assert.Equal((3, 0), (outcome.Ingest!.Ingest.Written, outcome.Ingest.Unresolved));
        var kyiv = Assert.Single(await db.Store.AsOfAsync(anna, "lives_in", new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(("Kyiv", "Anna lives in Kyiv.", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)), (kyiv.Value, kyiv.Text, kyiv.ValidFrom));
        var explanation = await db.Store.ExplainAsync(kyiv.Id);
        var source = Assert.Single(explanation!.Sources);
        Assert.Equal("moved to Kyiv", source.Quote);
        Assert.Equal(["l1", "l2"], JsonDocument.Parse(source.LocatorJson).RootElement.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()));

        Assert.Equal(3L, await db.CountAsync("entities"));
        var carolId = await Scalar<Guid>(db, "select id from facts.entities where name = 'CAROL'");
        Assert.Equal(2, (await db.Store.HistoryAsync(carolId, "likes")).Count);
    }

    [Fact]
    public async Task The_exact_name_resolver_reuses_one_entity_creates_when_none_and_refuses_when_two_share_a_name()
    {
        var (db, _, _) = await NewAsync();
        await using var _ = db;
        var resolver = new ExactNameEntityResolver(db.Store);

        // Idle pooled connections, so the 50 calls below start together instead of queueing behind connection set-up.
        var warm = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => db.Data.OpenConnectionAsync().AsTask()));
        foreach (var connection in warm)
        {
            await connection.DisposeAsync();
        }

        var names = new[] { "Carol", "carol", "  CAROL  ", "Carol" };
        var ids = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => resolver.ResolveAsync(new EntityMention("person", names[i % names.Length], null))));
        Assert.Single(ids.Distinct());
        Assert.NotNull(ids[0]);
        Assert.Equal(3L, await db.CountAsync("entities"));

        var place = await resolver.ResolveAsync(new EntityMention("place", "Carol", null));
        Assert.NotEqual(ids[0], place);

        var first = await db.Store.EnsureEntityAsync("person", "Dana", "ref:1");
        var second = await db.Store.EnsureEntityAsync("person", "dana", "ref:2");
        Assert.Null(await resolver.ResolveAsync(new EntityMention("person", "DANA", null)));
        await using (var merge = db.Data.CreateCommand($"update facts.entities set merged_into = '{first}' where id = '{second}'"))
        {
            await merge.ExecuteNonQueryAsync();
        }

        Assert.Equal(first, await resolver.ResolveAsync(new EntityMention("person", "dana", null)));
        Assert.Equal(Guid.Empty, await resolver.ResolveAsync(new EntityMention("person", "ignored", Guid.Empty)));
    }

    [Fact]
    public async Task The_resolver_is_asked_only_about_mentions_without_a_known_id_and_null_skips_the_fact()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var carol = await db.Store.EnsureEntityAsync("person", "Carol");
        var zed = Likes("jazz") with { Subject = Named("Zed") };
        var output = new ExtractedEpisode([Likes("tea"), zed, zed with { Value = "coffee", Text = "Zed likes coffee." }, Likes("rock") with { Subject = Named("Carol") }, Likes("pop") with { Subject = Named("carol") }]);
        Now(db);
        var validated = ExtractionValidator.Validate(output, Context(anna, bob));
        var resolver = new CountingResolver(name => name == "zed" ? null : carol);

        var result = await db.Store.IngestExtractedAsync(Conversation, validated, new ExtractorInfo("x", null), resolver);

        Assert.Equal(["carol", "zed"], resolver.Asked.Order());
        Assert.Equal((2, 3), (result.Unresolved, result.Ingest.Written));
        Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(11)));
        Assert.Equal(2, (await db.Store.AsOfAsync(carol, "likes", Harness.Day(11))).Count);
    }

    [Fact]
    public async Task An_accepted_replaces_retires_a_many_fact_and_links_the_successor()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var tea = await PlainLikeAsync(db, anna, "tea", "seed");
        var extractor = new ScriptedExtractor(Reply(Likes("coffee", replaces: "f1")));

        var outcome = await Extract(db, extractor, Conversation, Context(anna, bob, new KnownFact(tea, anna, "likes", "Anna likes tea.")));

        Assert.Equal((1, 0), (outcome.Ingest!.Replaced, outcome.Ingest.ReplacesSkipped));
        var history = await db.Store.HistoryAsync(anna, "likes");
        var coffee = Assert.Single(history, v => v.Fact.Value == "coffee");
        var old = Assert.Single(history, v => v.Fact.Value == "tea");
        Assert.Equal(("superseded", coffee.Fact.Id), (old.RetiredReason, old.SupersededBy));
        Assert.Equal(Harness.Day(10), old.RetiredAt);
        Assert.Equal("coffee", Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(11))).Value);
        Assert.Equal("tea", Assert.Single(await db.Store.AsOfAsync(anna, "likes", Harness.Day(11), Harness.Day(5))).Value);
        var why = await db.Store.ExplainAsync(tea);
        Assert.Equal(coffee.Fact.Id, Assert.Single(why!.Successors).Fact.Id);
    }

    [Fact]
    public async Task A_replaces_of_a_fact_retired_since_the_context_was_built_is_not_applied()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var tea = await PlainLikeAsync(db, anna, "tea", "seed");
        var context = Context(anna, bob, new KnownFact(tea, anna, "likes", "Anna likes tea."));
        await db.Store.RetractAsync(tea);

        var outcome = await Extract(db, new ScriptedExtractor(Reply(Likes("coffee", replaces: "f1"))), Conversation, context);

        Assert.Equal((1, 0, 1), (outcome.Ingest!.Ingest.Written, outcome.Ingest.Replaced, outcome.Ingest.ReplacesSkipped));
        var old = Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Value == "tea");
        Assert.Equal(("retracted", null), (old.RetiredReason, old.SupersededBy));
    }

    [Fact]
    public async Task A_third_party_episode_cannot_replace_an_owner_fact()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var tea = await PlainLikeAsync(db, anna, "tea", "seed");
        var episode = Conversation with { Trust = EpisodeTrust.ThirdParty };

        var outcome = await Extract(db, new ScriptedExtractor(Reply(Likes("coffee", replaces: "f1"))), episode, Context(anna, bob, new KnownFact(tea, anna, "likes", "Anna likes tea.")));

        Assert.Equal((1, 0, 1), (outcome.Ingest!.Ingest.Written, outcome.Ingest.Replaced, outcome.Ingest.ReplacesSkipped));
        Assert.Null(Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Value == "tea").RetiredAt);
    }

    [Fact]
    public async Task A_replaces_of_another_subjects_fact_is_not_applied_even_if_the_context_says_otherwise()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var annaTea = await PlainLikeAsync(db, anna, "tea", "seed");
        var bobCoffee = Likes("coffee", replaces: "f1") with { Subject = new ExtractedMention("e2", null, null) };

        var outcome = await Extract(db, new ScriptedExtractor(Reply(bobCoffee)), Conversation, Context(anna, bob, new KnownFact(annaTea, bob, "likes", "Bob likes tea.")));

        Assert.Equal((1, 0, 1), (outcome.Ingest!.Ingest.Written, outcome.Ingest.Replaced, outcome.Ingest.ReplacesSkipped));
        Assert.Null(Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Value == "tea").RetiredAt);
    }

    [Fact]
    public async Task A_replaces_is_not_applied_when_the_new_fact_is_retracted()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var tea = await PlainLikeAsync(db, anna, "tea", "seed");
        var coffee = await PlainLikeAsync(db, anna, "coffee", "seed-2");
        await db.Store.RetractAsync(coffee);

        var outcome = await Extract(db, new ScriptedExtractor(Reply(Likes("coffee", replaces: "f1"))), Conversation, Context(anna, bob, new KnownFact(tea, anna, "likes", "Anna likes tea.")));

        Assert.Equal((0, 0, 1), (outcome.Ingest!.Ingest.Written, outcome.Ingest.Replaced, outcome.Ingest.ReplacesSkipped));
        Assert.Null(Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Value == "tea").RetiredAt);
    }

    [Fact]
    public async Task Purging_the_episode_that_replaced_a_fact_revives_it()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var tea = await PlainLikeAsync(db, anna, "tea", "seed");
        var jazz = await PlainLikeAsync(db, anna, "jazz", "seed-2");
        await db.Store.RetractAsync(jazz);
        var extractor = new ScriptedExtractor(Reply(Likes("coffee", replaces: "f1")));
        await Extract(db, extractor, Conversation, Context(anna, bob, new KnownFact(tea, anna, "likes", "Anna likes tea.")));
        Assert.NotNull(Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Value == "tea").RetiredAt);

        var purged = await db.Store.PurgeSourceAsync("conversation", "c-1");

        Assert.Equal(1, purged.Episodes);
        var history = await db.Store.HistoryAsync(anna, "likes");
        Assert.DoesNotContain(history, v => v.Fact.Value == "coffee");
        var revived = Assert.Single(history, v => v.Fact.Value == "tea");
        Assert.Equal((null, null, null), (revived.RetiredAt, revived.RetiredReason, revived.SupersededBy));
        Assert.Equal("retracted", Assert.Single(history, v => v.Fact.Value == "jazz").RetiredReason);
    }

    [Fact]
    public async Task Purging_the_replacing_episode_leaves_a_replaced_fact_retired_when_its_assertion_is_live_again()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var tea = await PlainLikeAsync(db, anna, "tea", "seed");
        await Extract(db, new ScriptedExtractor(Reply(Likes("coffee", replaces: "f1"))), Conversation, Context(anna, bob, new KnownFact(tea, anna, "likes", "Anna likes tea.")));
        await db.Store.IngestAsync(new NewEpisode("conversation", "again", "again"), [new NewFact(anna, "likes", "tea", Harness.Day(2), Text: "Anna likes tea.")]);

        await db.Store.PurgeSourceAsync("conversation", "c-1");

        var teas = (await db.Store.HistoryAsync(anna, "likes")).Where(v => v.Fact.Value == "tea").ToList();
        Assert.Single(teas, v => v.RetiredAt is null);
        Assert.All(teas.Where(v => v.RetiredAt is not null), v => Assert.Null(v.SupersededBy));
    }

    [Fact]
    public async Task The_extractor_is_called_outside_any_database_transaction()
    {
        var name = await postgres.NewDatabaseNameAsync();
        var single = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringFor(name)) { MaxPoolSize = 1, Timeout = 3 }.ConnectionString;
        await using var data = NpgsqlDataSource.Create(single);
        await using (var ddl = data.CreateCommand(FactsSchema.Sql))
        {
            await ddl.ExecuteNonQueryAsync();
        }

        var store = new FactsStore(data, "test", new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Harness.Day(10)));
        await store.DefinePredicateAsync("likes", Cardinality.Many);
        var anna = await store.EnsureEntityAsync("person", "Anna");
        var extractor = new ScriptedExtractor(async token =>
        {
            // With a pool of one, this query waits for a free connection: it would time out if the pipeline still held its own.
            await using var command = data.CreateCommand("select 1");
            await command.ExecuteScalarAsync(token);
            return new ExtractorResponse(Reply(Likes("jazz")), new ExtractorInfo("x", null));
        });

        var outcome = await store.ExtractAsync(extractor, Conversation, Context(anna, Guid.NewGuid()));

        Assert.Equal(ExtractionStatus.Ingested, outcome.Status);
        Assert.Equal(1, outcome.Ingest!.Ingest.Written);
    }

    [Fact]
    public async Task Extracted_ingest_inside_a_caller_transaction_rolls_back_with_it()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        Now(db);
        var validated = ExtractionValidator.Validate(new ExtractedEpisode([LivesInKyiv, Likes("jazz")]), Context(anna, bob));
        await using (var connection = await db.Data.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var result = await db.Store.IngestExtractedAsync(connection, transaction, Conversation, validated, new ExtractorInfo("x", "m"), new ExactNameEntityResolver(db.Store));
            Assert.Equal(2, result.Ingest.Written);
            await transaction.RollbackAsync();
        }

        Assert.Equal((0L, 0L, 0L), (await db.CountAsync("episodes"), await db.CountAsync("facts"), await db.CountAsync("fact_sources")));
        Assert.Null(await db.Store.EpisodeStatusAsync("conversation", "c-1", "h1"));
    }

    [Fact]
    public async Task Recording_a_failure_never_overwrites_a_done_episode()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        await Extract(db, new ScriptedExtractor(Reply(Likes("jazz"))), Conversation, Context(anna, bob));

        Assert.False(await db.Store.RecordFailureAsync(Conversation, null, "extractor_error"));
        Assert.Equal("done", await Scalar<string>(db, "select status from facts.episodes"));

        var other = new NewEpisode("conversation", "c-9", "h9");
        Assert.True(await db.Store.RecordFailureAsync(other, new ExtractorInfo("x", "m", 5, 6), "timeout"));
        Assert.True(await db.Store.RecordFailureAsync(other, null, "bad_json"));
        Assert.Equal(("bad_json", 1L), (await Scalar<string>(db, "select failure from facts.episodes where source_ref = 'c-9'"), await Scalar<long>(db, "select count(*) from facts.episodes where source_ref = 'c-9'")));

        foreach (var bad in new[] { "", " ", "Has Space", "UPPER", "1abc", "with-dash", new string('a', 41), "été" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => db.Store.RecordFailureAsync(other, null, bad));
        }

        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var raw = db.Data.CreateCommand("update facts.episodes set failure = 'Anna told me 4417' where source_ref = 'c-9'");
            await raw.ExecuteNonQueryAsync();
        });
    }

    [Fact]
    public async Task Nothing_valid_is_a_done_episode_with_zero_facts_and_the_outcome_counts_the_rejections()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var reply = Reply(Likes("a") with { ValidFrom = "1850-01-01" }, Likes("b") with { ValidFrom = "2099-01-01" }, Likes("c") with { ValidFrom = "1800-05-05" });

        var outcome = await Extract(db, new ScriptedExtractor(reply), Conversation, Context(anna, bob));

        Assert.Equal(ExtractionStatus.Ingested, outcome.Status);
        Assert.Equal(0, outcome.Ingest!.Ingest.Written);
        Assert.Equal(3, outcome.Validation!.Rejections.Count(r => r.Reason == RejectReason.DateOutOfRange));
        Assert.Equal(EpisodeStatus.Done, await db.Store.EpisodeStatusAsync("conversation", "c-1", "h1"));
        Assert.Equal(0L, await db.CountAsync("facts"));
    }

    [Fact]
    public async Task Cancellation_propagates_and_records_nothing()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        using var cancel = new CancellationTokenSource();
        var extractor = new ScriptedExtractor(async token =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new ExtractorResponse("", new ExtractorInfo(null, null));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Extract(db, extractor, Conversation, Context(anna, bob), cancel.Token));

        Assert.Equal(0L, await db.CountAsync("episodes"));
    }

    [Fact]
    public async Task A_statement_is_stored_with_its_sentence_and_defines_the_predicate_once()
    {
        var (db, anna, bob) = await NewAsync();
        await using var _ = db;
        var statement = Likes("x") with { Predicate = "statement", Value = null, Text = "Anna is allergic to cats." };

        var outcome = await Extract(db, new ScriptedExtractor(Reply(statement, Likes("y") with { Predicate = "adores", Value = null, Text = "Anna adores opera." })), Conversation, Context(anna, bob));

        Assert.Equal((2, 1), (outcome.Ingest!.Ingest.Written, outcome.Validation!.Downgraded));
        var stored = await db.Store.AsOfAsync(anna, "statement", Harness.Day(11));
        Assert.Equal(["Anna adores opera.", "Anna is allergic to cats."], stored.Select(f => f.Value).Order());
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from facts.predicates where name = 'statement'"));
    }

    private sealed class CountingResolver(Func<string, Guid?> answer) : IEntityResolver
    {
        public ConcurrentBag<string> Asked { get; } = [];

        public Task<Guid?> ResolveAsync(EntityMention mention, CancellationToken cancellationToken = default)
        {
            var name = mention.Name.Trim().ToLowerInvariant();
            Asked.Add(name);
            return Task.FromResult(answer(name));
        }
    }
}

/// <summary>A scripted <see cref="IFactExtractor"/>: returns queued replies or throws queued errors, counting calls.</summary>
internal sealed class ScriptedExtractor : IFactExtractor
{
    private readonly Queue<Func<CancellationToken, Task<ExtractorResponse>>> _script = new();

    public ScriptedExtractor(params object[] script)
    {
        foreach (var step in script)
        {
            _script.Enqueue(step switch
            {
                string text => _ => Task.FromResult(new ExtractorResponse(text, new ExtractorInfo(null, "scripted"))),
                ExtractorResponse response => _ => Task.FromResult(response),
                Exception error => _ => throw error,
                _ => throw new ArgumentException("A script step is a reply, a response or an exception."),
            });
        }
    }

    public ScriptedExtractor(Func<CancellationToken, Task<ExtractorResponse>> always) => _script.Enqueue(always);

    public int Calls { get; private set; }

    public Task<ExtractorResponse> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return (_script.Count > 1 ? _script.Dequeue() : _script.Peek())(cancellationToken);
    }
}
