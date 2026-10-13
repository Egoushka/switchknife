using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Switchknife.Journal;
using Switchknife.McpGuard;

// Exercises each package the way a host would, under Native AOT. Exit 0 and one "ok" line per package.
var exit = 0;
foreach (var (package, smoke) in new (string, Func<Task<string?>>)[] { ("Switchknife.Journal", JournalAsync), ("Switchknife.McpGuard", McpGuardAsync) })
{
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

internal sealed record Made(string Name);

[JsonSerializable(typeof(Made))]
internal sealed partial class SmokeJson : JsonSerializerContext;
