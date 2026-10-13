using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using Switchknife.Journal;
using Switchknife.McpGuard;
using Switchknife.Migrations;

// Exercises each package the way a host would, under Native AOT. Exit 0 and one "ok" line per package.
// Switchknife.Migrations needs a PostgreSQL server: SWITCHKNIFE_SMOKE_POSTGRES is its connection string (any database
// on it; the case creates and drops its own). Unset, the case is skipped, except in CI where it fails.
var exit = 0;
var smokes = new (string, Func<Task<string?>>)[] { ("Switchknife.Journal", JournalAsync), ("Switchknife.McpGuard", McpGuardAsync), ("Switchknife.Migrations", MigrationsAsync) };
foreach (var (package, smoke) in smokes)
{
    if (package == "Switchknife.Migrations" && Environment.GetEnvironmentVariable("SWITCHKNIFE_SMOKE_POSTGRES") is null or ""
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
        return null;
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
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

internal sealed record Made(string Name);

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
