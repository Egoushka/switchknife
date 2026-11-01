using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Switchknife.Facts;
using Switchknife.Knowledge;
using Switchknife.Knowledge.Bench;

// recall  --connection C --gold F --corpus F [--systems b0,facts,kit] [--limit 10] [--out F]
// latency --connection C --corpus F --gold F [--namespaces 5] [--facts 50000] [--queries 200]
// The database name must contain "bench": the run replaces its facts schema.
var options = ParseOptions(args.Skip(1).ToArray());
if (args.Length == 0 || args[0] is not ("recall" or "latency") || !options.TryGetValue("connection", out var connection)
    || !options.TryGetValue("gold", out var goldPath) || !options.TryGetValue("corpus", out var corpusPath))
{
    Console.Error.WriteLine("usage: recall --connection C --gold F --corpus F [--systems b0,facts,kit] [--limit 10] [--out F]\n       latency --connection C --gold F --corpus F [--namespaces 5] [--facts 50000] [--queries 200]");
    return 2;
}

if (!new NpgsqlConnectionStringBuilder(connection).Database!.Contains("bench", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("the database name must contain \"bench\": this run replaces its facts schema.");
    return 2;
}

var decisions = Decision.Load(goldPath);
var corpus = Corpus.Load(corpusPath);
await using var data = NpgsqlDataSource.Create(connection);
return args[0] == "recall" ? await RecallAsync() : await LatencyAsync();

async Task ResetAsync()
{
    await using var cmd = data.CreateCommand("drop schema if exists facts cascade");
    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    await using var ddl = data.CreateCommand(FactsSchema.Sql);
    await ddl.ExecuteNonQueryAsync().ConfigureAwait(false);
}

async Task<int> RecallAsync()
{
    var systems = Option("systems", "b0,facts,kit").Split(',');
    var limit = int.Parse(Option("limit", "10"), CultureInfo.InvariantCulture);
    await ResetAsync().ConfigureAwait(false);
    await Corpus.LoadAsync(data, "prior-art", corpus, 1).ConfigureAwait(false);
    var store = new FactsStore(data, "prior-art");
    var kit = new KnowledgeSearch(data, ["prior-art"]);
    var b0 = new PlainText(corpus);

    var ceiling = decisions.Average(d => (double)d.Found(corpus.Select(f => f.Text + " " + f.SubjectName)).Count / d.Gold.Count);
    var report = new Dictionary<string, object>();
    var macro = new Dictionary<string, double>();
    foreach (var system in systems)
    {
        var rows = new List<object>();
        int found = 0, total = 0, nameOnly = 0;
        double sum = 0;
        foreach (var d in decisions)
        {
            IReadOnlyList<string> shown;
            IReadOnlyList<string> names = [];
            switch (system)
            {
                case "b0":
                    shown = b0.Search(d.Question, limit);
                    break;
                case "facts":
                    shown = (await store.SearchAsync(d.Question, default, null, limit).ConfigureAwait(false)).Select(h => h.Fact.Text).ToList();
                    break;
                case "kit":
                    var hits = await kit.SearchAsync(d.Question, limit: limit).ConfigureAwait(false);
                    shown = hits.Select(h => h.Name + "\n" + string.Join('\n', h.Facts.Select(f => f.Text))).ToList();
                    names = hits.Select(h => h.Name).ToList();
                    break;
                default:
                    throw new InvalidOperationException($"unknown system {system}.");
            }

            var hit = d.Found(shown);
            found += hit.Count;
            total += d.Gold.Count;
            sum += (double)hit.Count / d.Gold.Count;
            nameOnly += d.Found(names).Count;
            rows.Add(new { d.Id, d.Title, results = shown.Count, found = hit, missed = d.Gold.Except(hit).ToList() });
        }

        var (lo, hi) = Stats.Wilson(found, total);
        macro[system] = sum / decisions.Count;
        report[system] = new { recall_at_10 = Math.Round(macro[system], 4), micro = Math.Round((double)found / total, 4), wilson95 = new[] { Math.Round(lo, 4), Math.Round(hi, 4) }, found, total, name_only = Math.Round((double)nameOnly / total, 4), per_decision = rows };
        Console.WriteLine($"{system,-6} recall@{limit} = {macro[system]:0.000} (micro {found}/{total} = {(double)found / total:0.000}, Wilson 95% {lo:0.000} to {hi:0.000}), name only {(double)nameOnly / total:0.000}");
    }

    Console.WriteLine($"corpus: {corpus.Count} live facts, vectors: none; ceiling (gold names anywhere in the corpus, macro) {ceiling:0.000}");
    var verdict = "not measured";
    if (macro.TryGetValue("kit", out var score))
    {
        verdict = score >= Gates.Pass ? "PASS" : score < Gates.Stop ? "STOP" : "neither: between the stop and the pass line";
        Console.WriteLine($"K0 recall: {verdict} (pass at {Gates.Pass:0.00}, stop under {Gates.Stop:0.00})");
    }

    if (options.TryGetValue("out", out var outPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(new { date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), facts = corpus.Count, vectors = "none", ceiling = Math.Round(ceiling, 4), verdict, limit, systems = report }, new JsonSerializerOptions { WriteIndented = true }));
    }

    return verdict == "STOP" ? 3 : verdict == "PASS" ? 0 : 1;
}

async Task<int> LatencyAsync()
{
    var namespaces = int.Parse(Option("namespaces", "5"), CultureInfo.InvariantCulture);
    var perNamespace = int.Parse(Option("facts", "50000"), CultureInfo.InvariantCulture);
    var count = int.Parse(Option("queries", "200"), CultureInfo.InvariantCulture);
    await ResetAsync().ConfigureAwait(false);
    var names = Enumerable.Range(0, namespaces).Select(i => i == 0 ? "prior-art" : $"ns{i}").ToList();
    var copies = (perNamespace + corpus.Count - 1) / corpus.Count;
    var started = Stopwatch.GetTimestamp();
    foreach (var ns in names)
    {
        await Corpus.LoadAsync(data, ns, corpus, copies, perNamespace).ConfigureAwait(false);
    }

    Console.WriteLine($"loaded {namespaces} x {perNamespace} facts in {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s");
    await using (var analyze = data.CreateCommand("analyze facts.facts"))
    {
        await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    var kit = new KnowledgeSearch(data, names);
    for (var i = 0; i < 20; i++)
    {
        await kit.SearchAsync(decisions[i % decisions.Count].Question).ConfigureAwait(false);
    }

    var times = new List<double>();
    for (var i = 0; i < count; i++)
    {
        var t = Stopwatch.GetTimestamp();
        await kit.SearchAsync(decisions[i % decisions.Count].Question).ConfigureAwait(false);
        times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
    }

    times.Sort();
    var p95 = Stats.P95(times);
    Console.WriteLine($"knowledge_search over {namespaces} namespaces x {perNamespace} facts, {count} queries: p50 {times[times.Count / 2]:0.0} ms, p95 {p95:0.0} ms (gate {Gates.P95Milliseconds} ms, words only)");
    return p95 <= Gates.P95Milliseconds ? 0 : 1;
}

string Option(string name, string fallback) => options.TryGetValue(name, out var v) ? v : fallback;

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        result[args[i].TrimStart('-')] = args[i + 1];
    }

    return result;
}

internal static class Gates
{
    /// <summary>docs/benchmarks/knowledge-k0.md: B0 0.169 + 0.15.</summary>
    public const double Pass = 0.32;

    /// <summary>B0 + 0.10.</summary>
    public const double Stop = 0.27;

    public const int P95Milliseconds = 150;
}

/// <summary>B0: facts and their source names scored by the summed idf of the question's whole words they hold.</summary>
internal sealed partial class PlainText
{
    private readonly List<(string Text, HashSet<string> Words)> _docs;
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);

    public PlainText(IReadOnlyList<CorpusFact> facts)
    {
        _docs = facts.Select(f => (f.Text, Words(f.Text + " " + f.Source.Ref).ToHashSet(StringComparer.Ordinal))).ToList();
        foreach (var word in _docs.SelectMany(d => d.Words))
        {
            _df[word] = _df.GetValueOrDefault(word) + 1;
        }
    }

    public IReadOnlyList<string> Search(string question, int limit)
    {
        var terms = Words(question).Distinct().Where(_df.ContainsKey).ToList();
        return _docs.Select(d => (d.Text, Score: terms.Where(d.Words.Contains).Sum(t => Math.Log(1 + (_docs.Count - _df[t] + 0.5) / (_df[t] + 0.5)))))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(limit).Select(x => x.Text).ToList();
    }

    private static IEnumerable<string> Words(string text) => WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3);

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}
