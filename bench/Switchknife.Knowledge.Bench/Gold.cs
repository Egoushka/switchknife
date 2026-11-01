using System.Text.Json;
using System.Text.RegularExpressions;

namespace Switchknife.Knowledge.Bench;

/// <summary>One past decision: the question written without its answer, and the outside names its own text considered.</summary>
internal sealed record Decision(int Id, string Title, string Question, IReadOnlyList<string> Gold, IReadOnlyDictionary<string, string[]> Aliases)
{
    public static IReadOnlyList<Decision> Load(string path) => File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(line =>
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        return new Decision(
            r.GetProperty("id").GetInt32(), r.GetProperty("decision").GetString()!, r.GetProperty("question").GetString()!,
            r.GetProperty("gold").EnumerateArray().Select(x => x.GetString()!).ToList(),
            r.TryGetProperty("aliases", out var a) ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(x => x.GetString()!).ToArray()) : new Dictionary<string, string[]>());
    }).ToList();

    /// <summary>The gold names found in <paramref name="texts"/>: a name matches as a whole word, with space, hyphen and underscore alike.</summary>
    public IReadOnlyList<string> Found(IEnumerable<string> texts)
    {
        var haystack = string.Join('\n', texts).ToLowerInvariant();
        return Gold.Where(g => Spellings(g).Any(s => Matches(haystack, s))).ToList();
    }

    private IEnumerable<string> Spellings(string name) => Aliases.TryGetValue(name, out var more) ? more.Append(name) : [name];

    private static bool Matches(string haystack, string spelling)
    {
        var parts = Regex.Split(spelling.ToLowerInvariant().Trim(), @"[\s_-]+").Where(p => p.Length > 0).Select(Regex.Escape);
        var pattern = @"(?<![a-z0-9])" + string.Join(@"[\s_-]*", parts) + @"(?![a-z0-9])";
        return Regex.IsMatch(haystack, pattern);
    }
}
