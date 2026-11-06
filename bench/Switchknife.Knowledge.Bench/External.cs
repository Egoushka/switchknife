using System.Text.Json;

namespace Switchknife.Knowledge.Bench;

/// <summary>
/// Another retriever's answers, run outside this process and scored here with the same matcher.
/// The file is one JSON object: <c>{"results": [{"id": 1, "shown": ["..."], "names": ["..."]}], "timings_ms": [...]}</c>,
/// where <c>id</c> is the gold decision's id, <c>shown</c> is what the retriever showed best first, and <c>timings_ms</c> are its search
/// calls timed like <c>recall</c> times ours (one warm-up pass, then 10 passes of the questions, query vectors already computed).
/// </summary>
internal sealed class External(IReadOnlyDictionary<int, (IReadOnlyList<string> Shown, IReadOnlyList<string> Names)> results, IReadOnlyList<double> timingsMs)
{
    public IReadOnlyList<double> TimingsMs { get; } = timingsMs;

    /// <summary><c>NAME=FILE[,NAME=FILE]</c>.</summary>
    public static Dictionary<string, External> Parse(string spec) => spec.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(p => p[0], p => Load(p[1]), StringComparer.Ordinal);

    public (IReadOnlyList<string> Shown, IReadOnlyList<string> Names) Of(int id, int limit) =>
        results.TryGetValue(id, out var r) ? (r.Shown.Take(limit).ToList(), r.Names.Take(limit).ToList()) : ([], []);

    private static External Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var results = root.GetProperty("results").EnumerateArray().ToDictionary(
            r => r.GetProperty("id").GetInt32(),
            r => ((IReadOnlyList<string>)Strings(r, "shown"), (IReadOnlyList<string>)Strings(r, "names")));
        var timings = root.GetProperty("timings_ms").EnumerateArray().Select(t => t.GetDouble()).ToList();
        return new External(results, timings);
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
}
