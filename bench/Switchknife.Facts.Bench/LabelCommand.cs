using System.Globalization;
using System.Text.Json;

namespace Switchknife.Facts.Bench;

/// <summary>
/// <c>label</c>: the steps that build the G0 labelled set, one verb each. Every verb reads and writes files the caller
/// names and prints counts and ids only, never episode text, so its output is safe to paste into a report.
/// </summary>
internal static class LabelCommand
{
    public const string Usage = """
        label import  --kind nytka|feed --raw <tool output .json> --id <episode id> [--window 1] --episodes <episodes.jsonl>
        label plant   --episodes <file> --from <episode id> --id <new id> --at <ISO 8601> --replace "old=>new[||old=>new]"
        label prompt  --episodes <file> --out <folder>                          writes <id>.prompt.md for each episode
        label draft   --episodes <file> --outputs <folder of <id>.json> --gold <draft.jsonl>
        label edits   --draft <draft.jsonl> --reviewed <reviewed.jsonl> --gold <gold.jsonl>
        label pairs   --episodes <file> --gold <gold.jsonl> --out <pairs.jsonl>
        label check   --episodes <file> --gold <gold.jsonl>                     exits 1 when the set has a problem
        """;

    public static int Run(string verb, IReadOnlyDictionary<string, string> options)
    {
        string Required(string name) => options.GetValueOrDefault(name) is { Length: > 0 } value ? value : throw new ArgumentException($"--{name} is required.");

        switch (verb)
        {
            case "import":
                {
                    var raw = File.ReadAllText(Required("raw"));
                    var id = Required("id");
                    var episode = Required("kind") switch
                    {
                        Labelling.Nytka => Labelling.FromNytka(id, Parse(raw, LabelJson.Default.NytkaConversation), int.Parse(options.GetValueOrDefault("window") is { Length: > 0 } w ? w : "1", CultureInfo.InvariantCulture)),
                        Labelling.Feed => Labelling.FromFeed(id, Parse(raw, LabelJson.Default.FeedItem)),
                        var other => throw new ArgumentException($"--kind is nytka or feed, not '{other}'."),
                    };
                    Labelling.WriteEpisodes(Required("episodes"), [episode]);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{episode.Episode}\t{episode.Lines.Count} lines\t{episode.Characters} chars\t{Labelling.Cues(episode)} cues"));
                    return 0;
                }

            case "plant":
                {
                    var path = Required("episodes");
                    var from = Required("from");
                    var original = Labelling.ReadEpisodes(path).FirstOrDefault(e => e.Episode == from) ?? throw new ArgumentException($"No episode {from}.");
                    var replacements = Required("replace").Split("||").Select(r => r.Split("=>", 2)).Select(r => r.Length == 2 ? (r[0], r[1]) : throw new ArgumentException("--replace takes old=>new.")).ToList();
                    var planted = Labelling.Plant(original, Required("id"), DateTimeOffset.Parse(Required("at"), CultureInfo.InvariantCulture), replacements);
                    Labelling.WriteEpisodes(path, [planted]);
                    Console.WriteLine($"{planted.Episode}\tplanted from {from}");
                    return 0;
                }

            case "prompt":
                {
                    var folder = Directory.CreateDirectory(Required("out")).FullName;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var episode in Labelling.ReadEpisodes(Required("episodes")))
                    {
                        var (document, name) = Labelling.PromptDocument(episode);
                        File.WriteAllText(Path.Combine(folder, episode.Episode + ".prompt.md"), document);
                        names.Add(name);
                    }

                    Console.WriteLine($"extractor {string.Join(", ", names)}");
                    return 0;
                }

            case "draft":
                {
                    var drafts = new List<LabelledEpisode>();
                    foreach (var episode in Labelling.ReadEpisodes(Required("episodes")))
                    {
                        var file = Path.Combine(Required("outputs"), episode.Episode + ".json");
                        if (!File.Exists(file))
                        {
                            Console.WriteLine($"{episode.Episode}\tno output");
                            continue;
                        }

                        var (draft, validation, failure) = Labelling.Draft(episode, File.ReadAllText(file));
                        drafts.Add(draft);
                        var rejected = validation is null ? "" : string.Join(" ", validation.Rejections.GroupBy(r => r.Reason).Select(g => $"{g.Key}:{g.Count()}"));
                        Console.WriteLine(failure is { } f ? $"{episode.Episode}\t{f}" : string.Create(CultureInfo.InvariantCulture, $"{episode.Episode}\t{draft.Facts.Count} facts\t{rejected}"));
                    }

                    Labelling.WriteGold(Required("gold"), drafts);
                    return 0;
                }

            case "edits":
                {
                    var (gold, removed) = Labelling.MarkEdits(Scoring.ReadJsonLines(Required("draft")), Scoring.ReadJsonLines(Required("reviewed")));
                    Labelling.WriteGold(Required("gold"), gold);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{gold.Sum(e => e.Facts.Count)} facts, {gold.Sum(e => e.Facts.Count(f => f.Edited == true))} edited or added, {removed} removed"));
                    return 0;
                }

            case "pairs":
                {
                    var pairs = Labelling.Pairs(Scoring.ReadJsonLines(Required("gold")), Labelling.ReadEpisodes(Required("episodes")));
                    Labelling.WritePairs(Required("out"), pairs);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{pairs.Count} pairs, {pairs.Count(p => p.Planted)} planted"));
                    return 0;
                }

            case "check":
                {
                    var report = Labelling.Check(Scoring.ReadJsonLines(Required("gold")), Labelling.ReadEpisodes(Required("episodes")));
                    Console.WriteLine(JsonSerializer.Serialize(report, BenchJson.Default.LabelSetReport));
                    return report.Problems.Count == 0 ? 0 : 1;
                }

            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private static T Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(json, type) ?? throw new InvalidDataException("The tool output is null.");
}
