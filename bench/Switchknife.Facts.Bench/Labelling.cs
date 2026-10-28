using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Bench;

/// <summary>A source line of a candidate episode; the id is what an extractor cites.</summary>
internal sealed record CandidateLine(string Id, string Text, string? Speaker = null);

/// <summary>
/// One episode to label: a Nytka conversation window or a feed-eater item, cut into lines. A planted episode is an
/// edited copy of a real one (<see cref="PlantedFrom"/>), made where real text has too few supersession pairs.
/// </summary>
internal sealed record CandidateEpisode(
    string Episode,
    string Source,
    string Ref,
    DateTimeOffset OccurredAt,
    IReadOnlyList<CandidateLine> Lines,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Planted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PlantedFrom = null)
{
    [JsonIgnore]
    public int Characters => Lines.Sum(l => l.Text.Length);
}

/// <summary>What Nytka's conversation tool returns (the fields the labeller reads).</summary>
internal sealed record NytkaConversation(string Id, DateTimeOffset StartedAt, string? Transcript);

/// <summary>What feed-eater's item tool returns (the fields the labeller reads).</summary>
internal sealed record FeedItem(long Id, string? Title, DateTimeOffset PublishedAt, string? Content, string? Feed = null);

/// <summary>Two gold facts about one subject and a one-at-a-time predicate where the later value replaces the earlier.</summary>
internal sealed record SupersessionPair(string Subject, string Predicate, PairSide Earlier, PairSide Later, bool Planted);

internal sealed record PairSide(string Episode, string Object, string Day);

/// <summary>Counts for the G0 report; no text, so it is safe to print and send.</summary>
internal sealed record LabelSetReport(
    int Episodes, int NytkaEpisodes, int FeedEpisodes, int PlantedEpisodes, int Facts, int EditedFacts, int Pairs, int PlantedPairs,
    IReadOnlyList<string> Problems);

/// <summary>
/// Builds the G0 labelled set (docs/benchmarks/facts-library.md): candidate episodes from the two sources, the extraction
/// kit's prompt for each, a draft gold file from a model's kit output, supersession pairs, edit marks and a format check.
/// Text stays in the files the caller names; nothing here logs it.
/// </summary>
internal static partial class Labelling
{
    public const string Nytka = "nytka";
    public const string Feed = "feed";
    public const int MaxWindow = 4000;

    /// <summary>
    /// The predicates every system is offered on the labelled set. Fixed, so the prompt hash is the same for every episode
    /// and every run; a change here is a new labelled set.
    /// </summary>
    public static readonly IReadOnlyList<PredicateSpec> Predicates =
    [
        new("lives_in", Cardinality.One, "city or country where the subject lives"),
        new("located_in", Cardinality.One, "where a place, office or organisation is"),
        new("works_at", Cardinality.One, "the subject's employer or organisation"),
        new("role", Cardinality.One, "the subject's job title or role"),
        new("status", Cardinality.One, "current state of a project, product, ticket or account"),
        new("latest_version", Cardinality.One, "newest released version of a software product"),
        new("price", Cardinality.One, "what something costs"),
        new("works_on", Cardinality.Many, "a project or task the subject is working on"),
        new("uses", Cardinality.Many, "a tool, product or service the subject uses"),
        new("plans", Cardinality.Many, "something the subject intends to do"),
        new("likes", Cardinality.Many, "something the subject enjoys or prefers"),
        new("owns", Cardinality.Many, "something the subject has"),
        new("supports", Cardinality.Many, "a platform, feature or format a product supports"),
        new("requires", Cardinality.Many, "a dependency or minimum version something needs"),
        new("released", Cardinality.Event, "a product released a version or feature on a day"),
    ];

    /// <summary>Words that suggest a value changed; used to rank candidates before labelling, not to label.</summary>
    private static readonly string[] ChangeCues =
    [
        "moved", "now ", "no longer", "switched", "instead", "replaced", "deprecated", "renamed", "acquired", "new version",
        "released", "upgrade", "breaking", "used to", "anymore", "promoted", "joined", "left ", "quit",
        "переїх", "тепер", "вже не", "замість", "раніше", "переход", "переехал", "теперь", "уже не", "вместо", "раньше",
    ];

    // [08:54:57] Wearer: text
    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2})\]\s+(?<speaker>[^:]{1,40}):\s?(?<text>.*)$")]
    private static partial Regex TranscriptLine();

    /// <summary>
    /// Cuts a Nytka transcript into lines and returns window <paramref name="window"/> (from 1) of at most
    /// <paramref name="maxChars"/> characters of line text. Lines are never split, except one line longer than a whole
    /// window, which is cut at the limit. Text that continues on the next physical line joins its line.
    /// </summary>
    public static CandidateEpisode FromNytka(string episode, NytkaConversation conversation, int window = 1, int maxChars = MaxWindow)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var lines = new List<CandidateLine>();
        foreach (var raw in (conversation.Transcript ?? "").Split('\n'))
        {
            var match = TranscriptLine().Match(raw.TrimEnd('\r'));
            if (match.Success)
            {
                lines.Add(new CandidateLine("", match.Groups["text"].Value.Trim(), match.Groups["speaker"].Value.Trim()));
            }
            else if (raw.Trim().Length > 0 && lines.Count > 0)
            {
                lines[^1] = lines[^1] with { Text = lines[^1].Text + " " + raw.Trim() };
            }
            else if (raw.Trim().Length > 0)
            {
                lines.Add(new CandidateLine("", raw.Trim()));
            }
        }

        return new CandidateEpisode(episode, Nytka, conversation.Id, conversation.StartedAt, Window(lines, window, maxChars));
    }

    /// <summary>
    /// A feed item as lines: the feed's name (a release title such as "v0.8.5" does not name its product), the title,
    /// then one line per non-blank paragraph; the first window only.
    /// </summary>
    public static CandidateEpisode FromFeed(string episode, FeedItem item, int maxChars = MaxWindow)
    {
        ArgumentNullException.ThrowIfNull(item);
        var lines = new List<CandidateLine>();
        if (!string.IsNullOrWhiteSpace(item.Feed))
        {
            lines.Add(new CandidateLine("", item.Feed.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            lines.Add(new CandidateLine("", item.Title.Trim()));
        }

        lines.AddRange((item.Content ?? "").Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => new CandidateLine("", p)));
        return new CandidateEpisode(episode, Feed, item.Id.ToString(CultureInfo.InvariantCulture), item.PublishedAt, Window(lines, 1, maxChars));
    }

    /// <summary>Splits lines into consecutive windows within the budget, numbers the chosen one's lines l1…, and returns it.</summary>
    public static IReadOnlyList<CandidateLine> Window(IReadOnlyList<CandidateLine> lines, int window, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        var windows = new List<List<CandidateLine>> { new() };
        var used = 0;
        foreach (var line in lines)
        {
            var text = line.Text.Length > maxChars ? line.Text[..maxChars] : line.Text;
            if (used + text.Length > maxChars && windows[^1].Count > 0)
            {
                windows.Add([]);
                used = 0;
            }

            windows[^1].Add(line with { Text = text });
            used += text.Length;
        }

        if (window > windows.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, $"The source has {windows.Count} window(s).");
        }

        return [.. windows[window - 1].Select((l, i) => l with { Id = string.Create(CultureInfo.InvariantCulture, $"l{i + 1}") })];
    }

    /// <summary>
    /// An edited copy of a real episode, dated later, with each <c>old=&gt;new</c> replacement applied to every line.
    /// Throws when a replacement matches nothing, so a planted change never silently fails to happen.
    /// </summary>
    public static CandidateEpisode Plant(CandidateEpisode original, string episode, DateTimeOffset occurredAt, IReadOnlyList<(string Old, string New)> replacements)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacements);
        var lines = original.Lines.ToList();
        for (var i = 0; i < replacements.Count; i++)
        {
            var (old, @new) = replacements[i];
            if (old.Length == 0 || !lines.Any(l => l.Text.Contains(old, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Replacement {i + 1} matches no line of {original.Episode}.", nameof(replacements));
            }

            lines = [.. lines.Select(l => l with { Text = l.Text.Replace(old, @new, StringComparison.Ordinal) })];
        }

        return new CandidateEpisode(episode, original.Source, original.Ref, occurredAt, lines, Planted: true, PlantedFrom: original.Episode);
    }

    /// <summary>How many change cues an episode's text holds; ranks candidates for likely supersessions.</summary>
    public static int Cues(CandidateEpisode episode) =>
        episode.Lines.Sum(l => ChangeCues.Count(c => l.Text.Contains(c, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The extraction context for an episode: its lines and the fixed predicates; no known entities or facts.</summary>
    public static ExtractionContext Context(CandidateEpisode episode) =>
        new(episode.OccurredAt, episode.Lines.Select(l => new EvidenceLine(l.Id, l.Text, l.Speaker)), Predicates);

    /// <summary>The kit's prompt for one episode, as one markdown document: system, user, schema.</summary>
    public static (string Document, string ExtractorName) PromptDocument(CandidateEpisode episode)
    {
        var prompt = ExtractionPrompt.Build(Context(episode));
        var document = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<!-- episode {episode.Episode}, extractor {prompt.ExtractorName} -->\n\n")
            .Append("## System\n\n").Append(prompt.System).Append("\n\n")
            .Append("## User\n\n").Append(prompt.User).Append("\n\n")
            .Append("## Schema\n\n```json\n").Append(prompt.Schema).Append("\n```\n")
            .ToString();
        return (document, prompt.ExtractorName);
    }

    /// <summary>
    /// Turns a model's kit output into gold facts: parse, validate against the episode, then one triple per accepted fact.
    /// The subject and object are the names as written; <c>from</c> is set only when the output gave a start date.
    /// </summary>
    public static (LabelledEpisode Draft, ValidatedExtraction? Validation, ParseFailure? Failure) Draft(CandidateEpisode episode, string output)
    {
        ArgumentNullException.ThrowIfNull(episode);
        if (!ExtractionParser.TryParse(output, out var parsed, out var failure))
        {
            return (Labelled(episode, []), null, failure);
        }

        var validated = ExtractionValidator.Validate(parsed, Context(episode));
        var facts = validated.Facts.Select(f => new LabelledFact(
            f.Subject.Name,
            f.Predicate,
            f.Value ?? f.ObjectEntity?.Name ?? "",
            f.StartDefaulted ? null : Day(f.ValidFrom))).ToList();
        return (Labelled(episode, facts), validated, null);
    }

    private static LabelledEpisode Labelled(CandidateEpisode episode, IReadOnlyList<LabelledFact> facts) =>
        new(episode.Episode, facts) { Source = episode.Source, Planted = episode.Planted };

    /// <summary>
    /// Marks every fact of the reviewed gold that the draft did not have (same subject, predicate, object and start day,
    /// ignoring case) as edited, and clears the mark on facts the draft had. Facts the review only removed leave no row
    /// to mark; they are counted in the second value.
    /// </summary>
    public static (IReadOnlyList<LabelledEpisode> Gold, int Removed) MarkEdits(IReadOnlyList<LabelledEpisode> draft, IReadOnlyList<LabelledEpisode> reviewed)
    {
        var drafted = draft.ToDictionary(e => e.Episode, e => e.Facts, StringComparer.Ordinal);
        var reviewedIds = reviewed.Select(e => e.Episode).ToHashSet(StringComparer.Ordinal);
        var removed = draft.Where(e => !reviewedIds.Contains(e.Episode)).Sum(e => e.Facts.Count);
        var gold = new List<LabelledEpisode>();
        foreach (var episode in reviewed)
        {
            var left = (drafted.GetValueOrDefault(episode.Episode) ?? []).Select(DatedKey).ToList();
            var facts = new List<LabelledFact>();
            foreach (var fact in episode.Facts)
            {
                var hit = left.IndexOf(DatedKey(fact));
                if (hit >= 0)
                {
                    left.RemoveAt(hit);
                }

                facts.Add(fact with { Edited = hit < 0 ? true : null });
            }

            removed += left.Count;
            gold.Add(episode with { Facts = facts });
        }

        return (gold, removed);
    }

    /// <summary>
    /// Supersession pairs in a gold set: facts with the same subject and a one-at-a-time predicate (case-insensitive)
    /// whose objects differ, ordered by start day (or the episode's day when undated). Each change of value between
    /// neighbours in that order is one pair; a value restated later is not a pair.
    /// </summary>
    public static IReadOnlyList<SupersessionPair> Pairs(IReadOnlyList<LabelledEpisode> gold, IReadOnlyList<CandidateEpisode> episodes)
    {
        var one = Predicates.Where(p => p.Cardinality == Cardinality.One).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drawn = episodes.ToDictionary(e => e.Episode, e => e, StringComparer.Ordinal);
        var pairs = new List<SupersessionPair>();
        var groups = gold
            .SelectMany(e => e.Facts.Select(f => (Episode: e, Fact: f)))
            .Where(x => one.Contains(x.Fact.Predicate.Trim()))
            .GroupBy(x => (Norm(x.Fact.Subject), Norm(x.Fact.Predicate)));
        foreach (var group in groups)
        {
            var ordered = group
                .Select(x => (x.Episode, x.Fact, Day: x.Fact.ValidFrom ?? (drawn.TryGetValue(x.Episode.Episode, out var c) ? Day(c.OccurredAt) : "")))
                .OrderBy(x => x.Day, StringComparer.Ordinal)
                .ThenBy(x => x.Episode.Episode, StringComparer.Ordinal)
                .ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var (earlier, later) = (ordered[i - 1], ordered[i]);
                if (Norm(earlier.Fact.Object) == Norm(later.Fact.Object))
                {
                    continue;
                }

                pairs.Add(new SupersessionPair(
                    later.Fact.Subject.Trim(), later.Fact.Predicate.Trim(),
                    new PairSide(earlier.Episode.Episode, earlier.Fact.Object.Trim(), earlier.Day),
                    new PairSide(later.Episode.Episode, later.Fact.Object.Trim(), later.Day),
                    IsPlanted(earlier.Episode, drawn) || IsPlanted(later.Episode, drawn)));
            }
        }

        return pairs;
    }

    /// <summary>
    /// Counts and format problems of a gold set against its episodes: unique ids, every gold episode drawn and within the
    /// window, blank fields, unparsable days, predicates outside the catalog (other than <c>statement</c>), and the mix
    /// the procedure asks for (30 of each source, at least 25 pairs).
    /// </summary>
    public static LabelSetReport Check(IReadOnlyList<LabelledEpisode> gold, IReadOnlyList<CandidateEpisode> episodes)
    {
        var problems = new List<string>();
        var drawn = episodes.ToDictionary(e => e.Episode, e => e, StringComparer.Ordinal);
        var catalog = Predicates.Select(p => p.Name).Append(ExtractionValidator.StatementPredicate).ToHashSet(StringComparer.Ordinal);

        foreach (var duplicate in gold.GroupBy(e => e.Episode).Where(g => g.Count() > 1))
        {
            problems.Add($"{duplicate.Key}: appears {duplicate.Count()} times");
        }

        foreach (var episode in gold)
        {
            if (!drawn.TryGetValue(episode.Episode, out var source))
            {
                problems.Add($"{episode.Episode}: not in the episodes file");
            }
            else if (source.Characters > MaxWindow)
            {
                problems.Add($"{episode.Episode}: {source.Characters} characters, over {MaxWindow}");
            }

            for (var i = 0; i < episode.Facts.Count; i++)
            {
                var fact = episode.Facts[i];
                if (string.IsNullOrWhiteSpace(fact.Subject) || string.IsNullOrWhiteSpace(fact.Predicate) || string.IsNullOrWhiteSpace(fact.Object))
                {
                    problems.Add($"{episode.Episode} fact {i + 1}: blank subject, predicate or object");
                }
                else if (!catalog.Contains(fact.Predicate))
                {
                    problems.Add($"{episode.Episode} fact {i + 1}: predicate not in the catalog");
                }

                if (fact.ValidFrom is not null && !DateOnly.TryParseExact(fact.ValidFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    problems.Add($"{episode.Episode} fact {i + 1}: from is not YYYY-MM-DD");
                }
            }
        }

        var pairs = Pairs(gold, episodes);
        var sources = gold.Select(e => drawn.TryGetValue(e.Episode, out var c) ? c.Source : e.Source).ToList();
        var (nytka, feed) = (sources.Count(s => s == Nytka), sources.Count(s => s == Feed));
        if (nytka < 30 || feed < 30)
        {
            problems.Add($"mix: {nytka} Nytka and {feed} feed episodes, 30 of each needed");
        }

        if (pairs.Count < 25)
        {
            problems.Add($"pairs: {pairs.Count}, at least 25 needed");
        }

        return new LabelSetReport(
            gold.Count, nytka, feed,
            gold.Count(e => IsPlanted(e, drawn)),
            gold.Sum(e => e.Facts.Count),
            gold.Sum(e => e.Facts.Count(f => f.Edited == true)),
            pairs.Count,
            pairs.Count(p => p.Planted),
            problems);
    }

    public static IReadOnlyList<CandidateEpisode> ReadEpisodes(string path) =>
        File.Exists(path)
            ? [.. File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize(line, LabelJson.Default.CandidateEpisode) ?? throw new InvalidDataException("An episode line is null."))]
            : [];

    /// <summary>Adds or replaces episodes by id, keeping the file's order and appending new ones.</summary>
    public static void WriteEpisodes(string path, IEnumerable<CandidateEpisode> add)
    {
        var all = ReadEpisodes(path).ToList();
        foreach (var episode in add)
        {
            var at = all.FindIndex(e => e.Episode == episode.Episode);
            if (at >= 0)
            {
                all[at] = episode;
            }
            else
            {
                all.Add(episode);
            }
        }

        WriteLines(path, all.Select(e => JsonSerializer.Serialize(e, LabelJson.Default.CandidateEpisode)));
    }

    public static void WriteGold(string path, IEnumerable<LabelledEpisode> gold) =>
        WriteLines(path, gold.Select(e => JsonSerializer.Serialize(e, LabelJson.Default.LabelledEpisode)));

    public static void WritePairs(string path, IEnumerable<SupersessionPair> pairs) =>
        WriteLines(path, pairs.Select(p => JsonSerializer.Serialize(p, LabelJson.Default.SupersessionPair)));

    /// <summary>Writes through a temporary file and a rename, so a crash never leaves half a labelled set.</summary>
    private static void WriteLines(string path, IEnumerable<string> lines)
    {
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsPlanted(LabelledEpisode episode, Dictionary<string, CandidateEpisode> drawn) =>
        (drawn.TryGetValue(episode.Episode, out var c) ? c.Planted : episode.Planted) == true;

    private static string DatedKey(LabelledFact fact) =>
        string.Create(CultureInfo.InvariantCulture, $"{Norm(fact.Subject)}|{Norm(fact.Predicate)}|{Norm(fact.Object)}|{fact.ValidFrom}");

    private static string Norm(string value) => value.Trim().ToUpperInvariant();

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One compact JSON object per line, for the labelling files.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CandidateEpisode))]
[JsonSerializable(typeof(LabelledEpisode))]
[JsonSerializable(typeof(SupersessionPair))]
[JsonSerializable(typeof(NytkaConversation))]
[JsonSerializable(typeof(FeedItem))]
internal sealed partial class LabelJson : JsonSerializerContext;
