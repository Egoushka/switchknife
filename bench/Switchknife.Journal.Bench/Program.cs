using System.Globalization;
using System.Runtime.InteropServices;
using Npgsql;
using Switchknife.Journal;
using Switchknife.Journal.Bench;
using Switchknife.Journal.Postgres;

// run: Switchknife.Journal.Bench run --connection <string> [--scale 1.0] [--writers 8] [--rtt-ms 0,25,50] [--incompressible] [--no-sqlite]
//      (the database name must contain "bench": the run drops and recreates steps and step_events)
// Prints Markdown tables: hash cost, append latency per body size, and append latency across an emulated network delay.
if (args.Length == 0 || args[0] != "run")
{
    Console.Error.WriteLine("usage: run --connection <string> [--scale 1.0] [--writers 8] [--rtt-ms 0,25,50] [--incompressible] [--no-sqlite]");
    return 2;
}

var options = ParseOptions(args.Skip(1));
var connection = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("JOURNAL_BENCH_PG");
if (connection is null)
{
    Console.Error.WriteLine("run needs --connection or JOURNAL_BENCH_PG.");
    return 2;
}

var builder = new NpgsqlConnectionStringBuilder(connection);
if (!(builder.Database ?? "").Contains("bench", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to run against database '{builder.Database}': the run drops step tables, so its name must contain 'bench'.");
    return 2;
}

var scale = double.Parse(options.GetValueOrDefault("scale") ?? "1", CultureInfo.InvariantCulture);
var writers = int.Parse(options.GetValueOrDefault("writers") ?? "8", CultureInfo.InvariantCulture);
var rtts = (options.GetValueOrDefault("rtt-ms") ?? "0,25,50").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
var incompressible = options.ContainsKey("incompressible");
var sizes = Runner.DefaultSizes.Select(s => s with { Runs = Math.Max(100, (int)(s.Runs * scale)) }).ToArray();
var bodies = sizes.ToDictionary(s => s.Label, s => new Bodies(s.Bytes, 64, incompressible, seed: 1));

await using var data = NpgsqlDataSource.Create(connection);
await PgChainAppender.CreateSchemaAsync(data).ConfigureAwait(false);
await using var pgInfo = data.CreateCommand("select version() || ' | fsync=' || current_setting('fsync') || ' | synchronous_commit=' || current_setting('synchronous_commit')");
var serverInfo = (string)(await pgInfo.ExecuteScalarAsync().ConfigureAwait(false))!;

Console.WriteLine("## Environment");
Console.WriteLine();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"- Harness: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical CPUs, Release build"));
Console.WriteLine($"- Server: {serverInfo}");
Console.WriteLine($"- Bodies: {(incompressible ? "random hex (incompressible)" : "log-like text")}, JSON `{{tool,input,output}}`, 64 distinct per size; sizes {string.Join(", ", sizes.Select(s => s.Label))}");
Console.WriteLine($"- Runs per size (sequential): {string.Join(", ", sizes.Select(s => $"{s.Label} {s.Runs}"))}; warm-up 10% (min 50) dropped; {writers} writers in the concurrent scenarios (total runs halved)");
Console.WriteLine();

Console.WriteLine("## Hash cost (microseconds p50 / p95 / p99 per call)");
Console.WriteLine();
Console.WriteLine("| body | calls | body_hash = SHA-256(body) | chain link (envelope + body_hash + prev_hash) | SHA-256(body ‖ prev_hash) | body pass MB/s |");
Console.WriteLine("|---|---:|---|---|---|---:|");
foreach (var size in sizes)
{
    Console.WriteLine(Runner.HashRow(size, bodies[size.Label]));
}

Console.WriteLine();
var rows = new List<Row>();
var sqlitePath = Path.Combine(Path.GetTempPath(), $"journal-bench-{Guid.NewGuid():N}.db");
var appenders = new List<IAppender> { new MemoryChainAppender(), new PgChainAppender(data), new PlainAppender(new PgStepStore(data), "Postgres, unchained (PgStepStore today)") };
if (!options.ContainsKey("no-sqlite"))
{
    appenders.Add(new PlainAppender(await SqliteStepStore.OpenAsync(sqlitePath).ConfigureAwait(false), "SQLite file, unchained (SqliteStepStore today)"));
}

// Size outermost, stores innermost: a checkpoint or vacuum stall in the server lands on every store, not on whichever ran first.
foreach (var size in sizes)
{
    var b = bodies[size.Label];
    var scenarios = new (string Name, int Writers, bool Shared, int Total)[]
    {
        ("1 writer", 1, false, size.Runs),
        ($"{writers} writers, {writers} rooms", writers, false, size.Runs / 2),
        ($"{writers} writers, 1 room", writers, true, size.Runs / 2),
    };
    foreach (var scenario in scenarios)
    {
        foreach (var appender in appenders)
        {
            rows.Add(await Runner.RunAsync(appender, scenario.Name, size, b, scenario.Writers, scenario.Shared, scenario.Total).ConfigureAwait(false));
        }
    }
}

foreach (var appender in appenders)
{
    await appender.DisposeAsync().ConfigureAwait(false);
}

rows = [.. rows.OrderBy(r => appenders.FindIndex(a => a.Name == r.Store))];
File.Delete(sqlitePath);
Console.WriteLine("## Append latency (ms) and throughput");
Console.WriteLine();
Console.WriteLine("| store | scenario | body | n | p50 | p95 | p99 | max | appends/s |");
Console.WriteLine("|---|---|---|---:|---:|---:|---:|---:|---:|");
foreach (var r in rows)
{
    var l = r.Latency;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"| {r.Store} | {r.Scenario} | {r.Size} | {l.Count} | {l.P50:F2} | {l.P95:F2} | {l.P99:F2} | {l.Max:F1} | {l.PerSecond:F0} |"));
}

Console.WriteLine();
Console.WriteLine("## Chained Postgres append across an emulated network round trip (1 writer)");
Console.WriteLine();
Console.WriteLine("| round trip | body | n | p50 | p95 | p99 | max |");
Console.WriteLine("|---:|---|---:|---:|---:|---:|---:|");
foreach (var rtt in rtts.Where(r => r > 0))
{
    await using var relay = new DelayRelay(builder.Host!, builder.Port, TimeSpan.FromMilliseconds(rtt / 2.0));
    relay.Start();
    var remote = new NpgsqlConnectionStringBuilder(connection) { Host = "127.0.0.1", Port = relay.Port }.ConnectionString;
    await using var remoteData = NpgsqlDataSource.Create(remote);
    foreach (var size in sizes)
    {
        var row = await Runner.RunAsync(new PgChainAppender(remoteData), "1 writer", size, bodies[size.Label], 1, false, Math.Max(100, size.Runs / 10)).ConfigureAwait(false);
        var l = row.Latency;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {rtt} ms | {size.Label} | {l.Count} | {l.P50:F1} | {l.P95:F1} | {l.P99:F1} | {l.Max:F1} |"));
    }
}

return 0;

static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var parsed = new Dictionary<string, string>();
    var list = args.ToList();
    for (var i = 0; i < list.Count; i++)
    {
        if (!list[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = list[i][2..];
        parsed[key] = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal) ? list[++i] : "true";
    }

    return parsed;
}
