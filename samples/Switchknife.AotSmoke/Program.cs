using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Switchknife.Facts;
using Switchknife.Facts.Extraction;
using Switchknife.Facts.Testing;
using Switchknife.Hosting;
using Switchknife.Journal;
using Switchknife.Journal.Postgres;
using Switchknife.Knowledge;
using Switchknife.Knowledge.Approvals;
using Switchknife.Knowledge.Ideas;
using Switchknife.Knowledge.Tools;
using Switchknife.McpGuard;
using Switchknife.Migrations;
using Switchknife.Pace;
using Switchknife.SafeFetch;
using Switchknife.Testing.Http;

// Exercises each package the way a host would, under Native AOT. Exit 0 and one "ok" line per package.
// Switchknife.Migrations, Switchknife.Journal.Postgres, Switchknife.Facts and Switchknife.Knowledge need a PostgreSQL server: SWITCHKNIFE_SMOKE_POSTGRES
// is its connection string (any database on it; each case creates and drops its own; Switchknife.Facts needs the btree_gist
// extension available). Unset, those cases are skipped, except in CI where they fail.
var exit = 0;
var postgresCases = new HashSet<string> { "Switchknife.Facts", "Switchknife.Knowledge", "Switchknife.Migrations", "Switchknife.Journal.Postgres" };
var smokes = new (string, Func<Task<string?>>)[]
{
    ("Switchknife.Facts", FactsAsync),
    ("Switchknife.Facts.Extraction", FactsExtraction),
    ("Switchknife.Hosting", HostingAsync),
    ("Switchknife.Journal", JournalAsync),
    ("Switchknife.Journal.Postgres", JournalPostgresAsync),
    ("Switchknife.Knowledge", async () => Knowledge() ?? await KnowledgeSearchAsync()),
    ("Switchknife.Knowledge.Tools", KnowledgeToolsAsync),
    ("Switchknife.Knowledge.Approvals", () => Task.FromResult(KnowledgeApprovalsCheck())),
    ("Switchknife.McpGuard", McpGuardAsync),
    ("Switchknife.Migrations", MigrationsAsync),
    ("Switchknife.Pace", () => Task.FromResult(Pace())),
    ("Switchknife.SafeFetch", SafeFetchAsync),
    ("Switchknife.Testing.Http", TestingHttpAsync),
};
foreach (var (package, smoke) in smokes)
{
    if (postgresCases.Contains(package) && Environment.GetEnvironmentVariable("SWITCHKNIFE_SMOKE_POSTGRES") is null or ""
        && Environment.GetEnvironmentVariable("CI") != "true")
    {
        Console.WriteLine($"skip {package}: SWITCHKNIFE_SMOKE_POSTGRES is not set");
        continue;
    }

    if (await smoke() is { } why)
    {
        Console.Error.WriteLine($"fail {package}: {why}");
        exit = 1;
    }
    else
    {
        Console.WriteLine($"ok {package}");
    }
}
return exit;

static async Task<string?> KnowledgeSearchAsync()
{
    var server = Environment.GetEnvironmentVariable("SWITCHKNIFE_SMOKE_POSTGRES");
    if (string.IsNullOrEmpty(server))
        return "SWITCHKNIFE_SMOKE_POSTGRES is not set";

    var name = $"smoke_{Guid.NewGuid():N}";
    var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
    var database = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = false }.ConnectionString;
    await using (var create = new NpgsqlConnection(admin))
    {
        await create.OpenAsync();
        await using var createDb = new NpgsqlCommand($"create database {name}", create);
        await createDb.ExecuteNonQueryAsync();
    }
    try
    {
        await using (var migrate = new NpgsqlConnection(database))
        {
            await migrate.OpenAsync();
            await using var ddl = new NpgsqlCommand(FactsSchema.Sql, migrate);
            await ddl.ExecuteNonQueryAsync();
        }

        // A sentence that shares no full set of words with any fact still finds the entity whose facts it describes.
        await using var dataSource = NpgsqlDataSource.Create(database);
        var store = new FactsStore(dataSource, "smoke");
        await store.DefinePredicateAsync("note", Cardinality.Many);
        var jitter = await store.EnsureEntityAsync("tool", "Jitter");
        var bulkhead = await store.EnsureEntityAsync("tool", "Bulkhead");
        var day = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.IngestAsync(new NewEpisode("note", "1", "h1"), [
            new NewFact(jitter, "note", "jitter 0", day, Text: "Adds random delay to retries so many clients do not retry together."),
            new NewFact(bulkhead, "note", "bulkhead 0", day, Text: "Isolates pools so one slow dependency cannot drain every thread.")]);
        var hits = await new KnowledgeSearch(dataSource, ["smoke"]).SearchAsync("How do we stop many clients from retrying a failing service at the same moment?");

        return hits.Count > 0 && hits[0].Name == "Jitter" && hits[0].Facts.Count == 1
            ? null
            : $"{hits.Count} hit(s), first {(hits.Count > 0 ? hits[0].Name : null)}";
    }
    finally
    {
        await using var admin2 = new NpgsqlConnection(admin);
        await admin2.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists {name} with (force)", admin2);
        await drop.ExecuteNonQueryAsync();
    }
}

static async Task<string?> FactsAsync()
{
    var server = Environment.GetEnvironmentVariable("SWITCHKNIFE_SMOKE_POSTGRES");
    if (string.IsNullOrEmpty(server))
        return "SWITCHKNIFE_SMOKE_POSTGRES is not set";

    var name = $"smoke_{Guid.NewGuid():N}";
    var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
    var database = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = false }.ConnectionString;
    await using (var create = new NpgsqlConnection(admin))
    {
        await create.OpenAsync();
        await using var createDb = new NpgsqlCommand($"create database {name}", create);
        await createDb.ExecuteNonQueryAsync();
    }
    try
    {
        await using (var migrate = new NpgsqlConnection(database))
        {
            await migrate.OpenAsync();
            await using var ddl = new NpgsqlCommand(FactsSchema.Sql, migrate);
            await ddl.ExecuteNonQueryAsync();
        }

        // A value superseded later: asked as known before the second episode it is still the first, as known after it is the second.
        var clock = new SettableClock(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));
        await using var dataSource = NpgsqlDataSource.Create(database);
        var store = new FactsStore(dataSource, "smoke", clock);
        await store.DefinePredicateAsync("lives_in", Cardinality.One);
        var anna = await store.EnsureEntityAsync("person", "Anna", "smoke:people/1");
        await store.IngestAsync(new NewEpisode("note", "1", "h1"), [new NewFact(anna, "lives_in", "Kyiv", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))]);
        clock.Now = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
        var second = await store.IngestAsync(new NewEpisode("note", "2", "h2"), [new NewFact(anna, "lives_in", "Lviv", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero))]);
        var april = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var before = await store.AsOfAsync(anna, "lives_in", april, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var after = await store.AsOfAsync(anna, "lives_in", april);
        var history = await store.HistoryAsync(anna, "lives_in");

        // The same rows found by meaning: the nearest to a "Lviv" vector is Lviv as known now, and Kyiv as known before Lviv was learned.
        await store.SetEmbeddingsAsync("smoke", [.. history.Select(v => new FactEmbedding(v.Fact.Id, v.Fact.Value == "Kyiv" ? new float[] { 1, 0, 0, 0 } : new float[] { 0, 1, 0, 0 }))]);
        var nearNow = await store.NearestAsync("smoke", new float[] { 0, 1, 0, 0 }, 1, april);
        var nearThen = await store.NearestAsync("smoke", new float[] { 0, 1, 0, 0 }, 1, april, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        // A scripted model reply goes through the pipeline; the extractor and model it reports are read back from Explain.
        await store.DefinePredicateAsync("likes", Cardinality.Many);
        clock.Now = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var extractionContext = new ExtractionContext(
            new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero),
            [new EvidenceLine("l1", "I like jazz.", "Anna")],
            [new PredicateSpec("likes", Cardinality.Many, "what a person likes")],
            [new KnownEntity(anna, "person", "Anna")]);
        var extracted = await store.ExtractAsync(new ScriptedExtractor(new ExtractorResponse(
            """{"facts":[{"subject":{"ref":"e1","name":null,"kind":null},"predicate":"likes","value":"jazz","object":null,"text":"Anna likes jazz.","validFrom":"2026-05-01","validTo":null,"replaces":null,"evidence":["l1"],"quote":"like jazz","salience":0.8}]}""",
            new ExtractorInfo(null, "smoke-model", 10, 5))), new NewEpisode("note", "3", "h3", extractionContext.OccurredAt), extractionContext);
        var liked = await store.AsOfAsync(anna, "likes", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var why = liked.Count == 1 ? await store.ExplainAsync(liked[0].Id) : null;
        var source = why?.Sources.SingleOrDefault();

        return (before.SingleOrDefault()?.Value, after.SingleOrDefault()?.Value, history.Count, second.Written, nearNow.SingleOrDefault()?.Fact.Value, nearThen.SingleOrDefault()?.Fact.Value,
                extracted.Status, source?.Extractor?.StartsWith("kit/1+", StringComparison.Ordinal), source?.Model)
            == ("Kyiv", "Lviv", 3, 1, "Lviv", "Kyiv", ExtractionStatus.Ingested, true, "smoke-model")
            ? null
            : $"extraction {extracted.Status}/{extracted.FailureCode}, {liked.Count} liked, extractor {source?.Extractor}, model {source?.Model}; as known before: {before.SingleOrDefault()?.Value}, after: {after.SingleOrDefault()?.Value}, {history.Count} row(s), {second.Written} written, nearest now {nearNow.SingleOrDefault()?.Fact.Value}, then {nearThen.SingleOrDefault()?.Fact.Value}";
    }
    finally
    {
        await using var admin2 = new NpgsqlConnection(admin);
        await admin2.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists {name} with (force)", admin2);
        await drop.ExecuteNonQueryAsync();
    }
}

static Task<string?> FactsExtraction()
{
    // A model's reply as it comes (fenced, with a sentence before it) is read, checked against the episode and turned into a typed fact; the prompt is built.
    var anna = Guid.NewGuid();
    var context = new ExtractionContext(
        new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero),
        [new EvidenceLine("l1", "I moved to Kyiv in March.", "Anna")],
        [new PredicateSpec("lives_in", Cardinality.One, "where a person lives")],
        [new KnownEntity(anna, "person", "Anna")]);
    const string reply = """
        Here is what I found:
        ```json
        {"facts":[{"subject":{"ref":"e1","name":null,"kind":null},"predicate":"lives_in","value":"Kyiv","object":null,"text":"Anna lives in Kyiv.","validFrom":"2026-03-01","validTo":null,"replaces":null,"evidence":["l1"],"quote":"moved to Kyiv","salience":0.9}]}
        ```
        """;
    if (!ExtractionParser.TryParse(reply, out var episode, out var failure))
        return Task.FromResult<string?>($"parse failed: {failure}");

    var validated = ExtractionValidator.Validate(episode, context);
    var prompt = ExtractionPrompt.Build(context);
    var fact = validated.Facts.SingleOrDefault();
    return Task.FromResult<string?>(
        (fact?.Predicate, fact?.Value, fact?.Subject.KnownId, fact?.LocatorJson, validated.Rejections.Count, prompt.User.Contains("[l1] Anna: I moved", StringComparison.Ordinal), prompt.ExtractorName.StartsWith("kit/1+", StringComparison.Ordinal))
            == ("lives_in", "Kyiv", anna, """{"evidence":["l1"]}""", 0, true, true)
            ? null
            : $"accepted {validated.Facts.Count}, rejected {validated.Rejections.Count}, extractor {prompt.ExtractorName}");
}

static async Task<string?> HostingAsync()
{
    // A real host, as a service runs it: the first poll throws, the host keeps running, the second poll succeeds.
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    var log = new LineLogger();
    var poller = new FlakyPoller(log);
    builder.Services.AddHostedService(_ => poller);
    using var host = builder.Build();
    await host.StartAsync();
    var recovered = await Task.WhenAny(poller.Recovered.Task, Task.Delay(TimeSpan.FromSeconds(10))) == poller.Recovered.Task;
    var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;
    await host.StopAsync();
    var logged = log.Lines.Any(l => l.StartsWith("flaky poll failed (1 in a row)", StringComparison.Ordinal));
    return (recovered, stopping, logged, poller.ExecuteTask?.Status) == (true, false, true, TaskStatus.RanToCompletion)
        ? null
        : $"recovered {recovered}, host stopping {stopping}, failure logged {logged}, loop {poller.ExecuteTask?.Status}";
}

static string? Pace()
{
    var clock = new SettableClock(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
    var policy = new Policy { Consumers = new Dictionary<string, ConsumerPolicy> { ["job"] = new(0.25), ["owner"] = new(0.75) }, Mode = PaceMode.Enforce };
    var ledger = new Ledger(policy, clock);
    var reset = clock.GetUtcNow().AddDays(7);
    ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.0, reset));
    clock.Now += TimeSpan.FromDays(1);
    ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.0, reset));
    var lease = ledger.Admit(new AdmitRequest("job", 1));
    if (lease.Decision != Decision.Allow || lease.LeaseId is null)
        return $"first admit: {lease.Decision}";
    ledger.Report(new UsageReport("job", new Dictionary<string, double> { ["m"] = 10 }, lease.LeaseId));
    ledger.Observe(new MeterReading(MeterWindow.SevenDay, 0.08, reset));
    var held = ledger.Admit(new AdmitRequest("job"));
    var restored = new Ledger(policy, clock, ledger.Snapshot()).Status().Consumers["job"];
    return held.Decision == Decision.Hold && held.RetryAfter > TimeSpan.Zero && Math.Abs(restored.Used - 0.08) < 1e-9
        ? null
        : $"over its share: {held.Decision}, used {restored.Used}";
}

static string? Knowledge()
{
    var test = new CheapestTest("landing page", 6m, 0m, "signups", "10 in 14 days");
    var hypothesis = new ValueHypothesis("freelancers", "save 2 h a week", "3 interviews", 5000m);
    var gaps = IdeaValidator.Validate(IdeaStatus.Seed, new IdeaDraft(IdeaStatus.Testing, ValueKind.Money, hypothesis, test));
    var refused = IdeaValidator.Validate(IdeaStatus.Seed, new IdeaDraft(IdeaStatus.Testing));
    var back = IdeaJson.ReadTest(IdeaJson.Write(test));
    var back2 = IdeaJson.ReadHypothesis(IdeaJson.Write(hypothesis));
    var board = System.Text.Json.JsonSerializer.Serialize(
        new IdeaBoard(new StatusCounts(1, 0, 0, 0, 0), new IdeaFunnel(1, 0, 0, 0, 0, null, null), new StageDays(null, null, null), [], [], new IdeaMoney(0, 0, 0, null), null, null, []),
        IdeaBoardJsonContext.Default.IdeaBoard);
    var issue = GitHubIssueAdapter.FromPayload("""{"action":"edited","issue":{"number":7,"title":"Grade ideas","body":"b"},"repository":{"full_name":"o/r"}}""");
    return gaps.Count == 0 && refused.Count == 3 && back == test && back2 == hypothesis && board.Contains("\"late_yield\":null", StringComparison.Ordinal) && issue is { Episode.SourceRef: "o/r#7", Title: "Grade ideas" }
        ? null
        : $"gaps {gaps.Count}, refused {refused.Count}, test {back}, hypothesis {back2}, issue {issue}, board {board}";
}

static async Task<string?> KnowledgeToolsAsync()
{
    await using var data = NpgsqlDataSource.Create("Host=unused");
    var tools = KnowledgeTools.All(new IdeaStore(data));
    var properties = tools[0].InputSchema["properties"]?.AsObject().Count ?? 0;
    var refused = await tools[0].Handler("not json", CancellationToken.None);
    return tools.Count == 2 && tools.All(t => t.Private) && properties == 9 && refused.Contains("\"invalid\"", StringComparison.Ordinal)
        ? null
        : $"tools {tools.Count}, properties {properties}, refused {refused}";
}

static string? KnowledgeApprovalsCheck()
{
    var test = new CheapestTest("landing page", 6m, 0m, "signups", "10 in 14 days");
    var item = KnowledgeApprovals.ResultItem(new ResultProposal(Guid.NewGuid(), test), new ItemDelivery("https://hooks.example/k"));
    var json = System.Text.Json.JsonSerializer.Serialize(item, ApprovalJsonContext.Default.ApprovalItem);
    var decision = KnowledgeApprovals.ReadDecision($$"""{"decision":"d1","id":"{{item.Id}}","action":"passed","edited":{"body.uah":"3000"},"at":"2026-11-01T10:00:00Z"}""")!;
    var signed = decision with { Sig = ApprovalSignature.Sign("k", decision) };
    var parsed = KnowledgeApprovals.ParseResult(item, signed);
    return json.Contains("\"pass_line\":\"10 in 14 days\"", StringComparison.Ordinal) && ApprovalSignature.Verify("k", signed) && parsed is { Accepted: true, Uah: 3000m }
        ? null
        : $"item {json}, parsed {parsed}";
}

static async Task<string?> JournalAsync()
{
    var dir = Directory.CreateTempSubdirectory("switchknife-aot").FullName;
    try
    {
        var path = Path.Combine(dir, "journal.db");
        await using (var journal = await StepJournal.OpenAsync(path))
        {
            var made = await journal.RunAsync("flow", "step", (c, _) => Task.FromResult(new Made($"made-{c.Attempt}")), SmokeJson.Default.Made);
            await journal.AppendEventAsync("flow", "made", """{"name":"made-1"}""");
            if (made.Name != "made-1")
                return $"first run returned {made.Name}";
        }
        await using (var journal = await StepJournal.OpenAsync(path))
        {
            var again = await journal.RunAsync("flow", "step", (_, _) => Task.FromResult(new Made("ran-twice")), SmokeJson.Default.Made);
            var events = await journal.ReadEventsAsync("flow");
            if (again.Name != "made-1" || events.Count != 1)
                return $"after reopen: {again.Name}, {events.Count} events";
        }
        await using (var journal = await StepJournal.OpenAsync(path))
        {
            var reset = await journal.ResetAsync("flow", "step");
            var rerun = await journal.RunAsync("flow", "step", (c, _) => Task.FromResult(new Made($"made-{c.Attempt}-{c.Resumed}")), SmokeJson.Default.Made);
            if (!reset || rerun.Name != "made-1-False" || await journal.ResetAsync("flow", "missing"))
                return $"after reset: {reset}, {rerun.Name}";
        }
        await using (var journal = await StepJournal.OpenAsync(path))
            return await EventFeedAsync(journal);
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}

// Keyed append, tag read and signal: the same checks on SQLite and Postgres.
static async Task<string?> EventFeedAsync(StepJournal journal)
{
    var wake = journal.Signal.WaitAsync(0);
    var batch = new List<NewEvent> { new("seen", "{}", Key: "a", Tag: "t"), new("seen", "{}", Key: "b"), new("seen", "{}", Key: "a", Tag: "t") };
    var seqs = await journal.AppendEventsAsync("feed", batch);
    var woken = await wake.WaitAsync(TimeSpan.FromSeconds(10));
    var tagged = await journal.ReadAllEventsAsync(tag: "t");
    return seqs[0] == seqs[2] && seqs[0] != seqs[1] && woken == seqs[1] && tagged.Count == 1 && tagged[0].Tag == "t"
        ? null
        : $"event feed: {string.Join(",", seqs)}, woken {woken}, {tagged.Count} tagged";
}

static async Task<string?> JournalPostgresAsync()
{
    var server = Environment.GetEnvironmentVariable("SWITCHKNIFE_SMOKE_POSTGRES");
    if (string.IsNullOrEmpty(server))
        return "SWITCHKNIFE_SMOKE_POSTGRES is not set";

    var name = $"smoke_{Guid.NewGuid():N}";
    var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
    var database = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = false }.ConnectionString;
    await using (var create = new NpgsqlConnection(admin))
    {
        await create.OpenAsync();
        await using var createDb = new NpgsqlCommand($"create database {name}", create);
        await createDb.ExecuteNonQueryAsync();
    }
    try
    {
        await using (var migrate = new NpgsqlConnection(database))
        {
            await migrate.OpenAsync();
            await using var ddl = new NpgsqlCommand(PgStepStoreSchema.Sql, migrate);
            await ddl.ExecuteNonQueryAsync();
        }

        await using var dataSource = NpgsqlDataSource.Create(database);
        await using var journal = StepJournal.Open(new PgStepStore(dataSource));
        var made = await journal.RunAsync("flow", "step", (c, _) => Task.FromResult(new Made($"made-{c.Attempt}")), SmokeJson.Default.Made);
        var again = await journal.RunAsync("flow", "step", (_, _) => Task.FromResult(new Made("ran-twice")), SmokeJson.Default.Made);
        return (made.Name, again.Name) == ("made-1", "made-1")
            ? await EventFeedAsync(journal)
            : $"first run {made.Name}, second run {again.Name}";
    }
    finally
    {
        await using var admin2 = new NpgsqlConnection(admin);
        await admin2.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists {name} with (force)", admin2);
        await drop.ExecuteNonQueryAsync();
    }
}

static async Task<string?> McpGuardAsync()
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    await using var app = builder.Build();
    RequestDelegate reached = context => context.Response.WriteAsync("reached");
    app.MapPost("/mcp", reached).RequireMcpGuard("smoke-token");
    await app.StartAsync();

    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    async Task<HttpStatusCode> PostAsync(string? token, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        if (token is not null)
            request.Headers.Add("Authorization", $"Bearer {token}");
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    var (ok, wrong, browser) = (await PostAsync("smoke-token", null), await PostAsync("nope", null), await PostAsync("smoke-token", "https://evil.example"));
    await app.StopAsync();
    return (ok, wrong, browser) == (HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)
        ? null
        : $"token {ok}, wrong token {wrong}, origin {browser}";
}

static async Task<string?> MigrationsAsync()
{
    var server = Environment.GetEnvironmentVariable("SWITCHKNIFE_SMOKE_POSTGRES");
    if (string.IsNullOrEmpty(server))
        return "SWITCHKNIFE_SMOKE_POSTGRES is not set";

    var name = $"smoke_{Guid.NewGuid():N}";
    var database = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = false }.ConnectionString;
    try
    {
        // A missing database is created, both scripts run in name order, and a second run finds nothing to do.
        var log = new LineLogger();
        var migrator = new DatabaseMigrator(database, typeof(Made).Assembly, log);
        var first = migrator.Run();
        var second = migrator.Run();
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand("select name from smoke_widgets where id = 1", connection);
        var widget = await read.ExecuteScalarAsync() as string;
        var names = string.Join(",", first.Select(n => n[(n.IndexOf(".Migrations.", StringComparison.Ordinal) + 12)..]));
        var logged = log.Lines.Any(l => l.Contains("0002_first_widget.sql", StringComparison.Ordinal));
        return (names, second.Count, widget, logged) == ("0001_widgets.sql,0002_first_widget.sql", 0, "first", true)
            ? null
            : $"first run {names}, second run {second.Count} script(s), widget {widget}, script logged {logged}";
    }
    finally
    {
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists {name} with (force)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

static async Task<string?> TestingHttpAsync()
{
    var stub = StubHttpHandler.Sequence(
        _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        request => StubHttpHandler.Json($$"""{"path":"{{request.RequestUri!.AbsolutePath}}"}"""));
    using var http = stub.Client("https://api.example.test/");
    using var request = new HttpRequestMessage(HttpMethod.Post, "items") { Content = new StringContent("payload") };
    request.Headers.Add("Authorization", "Bearer smoke");
    using var first = await http.SendAsync(request);
    using var second = await http.GetAsync("items/1");
    var json = await second.Content.ReadAsStringAsync();
    var (method, uri, body, headers) = stub.Requests[0];
    return (first.StatusCode, json, method, uri.PathAndQuery, body, headers.GetValueOrDefault("authorization"), stub.Requests.Count)
        == (HttpStatusCode.ServiceUnavailable, """{"path":"/items/1"}""", HttpMethod.Post, "/items", "payload", "Bearer smoke", 2)
        ? null
        : $"first {first.StatusCode}, second {json}, recorded {method} {uri} {body} {headers.GetValueOrDefault("authorization")}, {stub.Requests.Count} request(s)";
}

static async Task<string?> SafeFetchAsync()
{
    var stub = new StubHttpHandler((request, _) => request.RequestUri!.AbsolutePath == "/go"
        ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://10.0.0.1/admin") } }
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<title>T</title><article><p>Body</p></article>", System.Text.Encoding.UTF8, "text/html") });
    using var http = stub.Client();
    using var handler = GuardedFetcher.CreateHandler(["feeds.internal"]);   // the connect callback must build under AOT too
    var fetcher = new GuardedFetcher(http, new SafeFetchOptions { HostGap = TimeSpan.Zero });
    var page = await fetcher.FetchAsync(new Uri("https://example.test/post"), default);
    var redirect = await fetcher.FetchAsync(new Uri("https://example.test/go"), default);
    var text = page.Ok ? ReadableText.Extract(page.Body!, 1000) : "";
    return (IpGuard.IsPublic(IPAddress.Parse("8.8.8.8")), IpGuard.IsPublic(IPAddress.Parse("::ffff:127.0.0.1")), text.StartsWith("T\n\n", StringComparison.Ordinal) && text.EndsWith("Body", StringComparison.Ordinal), redirect.Outcome, stub.Requests.Count)
        == (true, false, true, FetchOutcome.Refused, 2)
        ? null
        : $"page {page.Outcome} '{text}', redirect {redirect.Outcome}, {stub.Requests.Count} request(s)";
}

internal sealed record Made(string Name);

internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FlakyPoller(ILogger logger) : PollingService(TimeProvider.System, logger)
{
    private int _polls;

    public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override string Name => "flaky";

    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(10);

    protected override Task PollAsync(CancellationToken cancellationToken) =>
        ++_polls == 1 ? throw new InvalidOperationException("first poll fails") : Task.CompletedTask;

    protected override Task OnSucceededAsync(CancellationToken cancellationToken)
    {
        Recovered.TrySetResult();
        return Task.CompletedTask;
    }
}

internal sealed class LineLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception));
}

[JsonSerializable(typeof(Made))]
internal sealed partial class SmokeJson : JsonSerializerContext;
