using System.Globalization;
using System.Text.Json;
using Npgsql;
using Switchknife.Facts.Bench;

// run:   Switchknife.Facts.Bench run --connection <string> [--subjects 2000] [--facts 50000] [--queries 2000]
//        [--episode-size 5] [--vector-dims 0] [--vector-noise 1.0] [--oversample 40] [--seed 1] [--fresh] [--gate]    (the database name must contain "bench")
//        --gate also exits 3 when a run exceeds the ADR's latency or size thresholds (Gates.Adr), naming each on stderr
// score: Switchknife.Facts.Bench score --gold gold.jsonl --pred pred.jsonl
if (args.Length == 0 || args[0] is not ("run" or "score"))
{
    Console.Error.WriteLine("usage: run --connection <string> [options] | score --gold <file> --pred <file>");
    return 2;
}

var options = ParseOptions(args.Skip(1));

if (args[0] == "score")
{
    var score = Scoring.Score(Scoring.ReadJsonLines(Required("gold")), Scoring.ReadJsonLines(Required("pred")));
    Console.WriteLine(JsonSerializer.Serialize(score, BenchJson.Default.ExtractionScore));
    return 0;
}

var connection = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("FACTS_BENCH_PG");
if (connection is null)
{
    Console.Error.WriteLine("run needs --connection or FACTS_BENCH_PG.");
    return 2;
}

var database = new NpgsqlConnectionStringBuilder(connection).Database ?? "";
if (!database.Contains("bench", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Refusing to run against database '{database}': the run replaces the facts schema, so its name must contain 'bench'.");
    return 2;
}

await using var data = NpgsqlDataSource.Create(connection);
var report = await Runner.RunAsync(data, new RunOptions(
    Int("subjects", 2000), Int("facts", 50_000), Int("queries", 2000), Int("episode-size", 5), Int("vector-dims", 0), Int("seed", 1), Double("vector-noise", 1.0), Int("oversample", 40)),
    options.ContainsKey("fresh")).ConfigureAwait(false);
Console.WriteLine(JsonSerializer.Serialize(report, BenchJson.Default.RunReport));
if (report.AsOfWrong + report.AsOfOverlap != 0)
{
    return 1;
}

var failures = options.ContainsKey("gate") ? Gates.Adr.Check(report) : [];
foreach (var failure in failures)
{
    Console.Error.WriteLine($"gate failed: {failure}");
}

return failures.Count == 0 ? 0 : 3;

static Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    string? key = null;
    foreach (var argument in arguments)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            key = argument[2..];
            parsed[key] = "";
        }
        else if (key is not null)
        {
            parsed[key] = argument;
        }
    }

    return parsed;
}

string Required(string name) => options.GetValueOrDefault(name) is { Length: > 0 } value ? value : throw new ArgumentException($"--{name} is required.");

double Double(string name, double fallback) => options.TryGetValue(name, out var text) && text.Length > 0 ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;

int Int(string name, int fallback) => options.TryGetValue(name, out var text) && text.Length > 0 ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
