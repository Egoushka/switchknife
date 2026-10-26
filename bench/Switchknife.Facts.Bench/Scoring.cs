using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchknife.Facts.Bench;

/// <summary>A fact as a labelled set or an extractor states it: who, which predicate, which value, and the day it began.</summary>
internal sealed record LabelledFact(
    [property: JsonPropertyName("s")] string Subject,
    [property: JsonPropertyName("p")] string Predicate,
    [property: JsonPropertyName("o")] string Object,
    [property: JsonPropertyName("from")] string? ValidFrom = null);

/// <summary>The facts of one episode; one JSON object per line of a gold or predicted file.</summary>
internal sealed record LabelledEpisode(
    [property: JsonPropertyName("episode")] string Episode,
    [property: JsonPropertyName("facts")] IReadOnlyList<LabelledFact> Facts);

/// <summary>Extraction quality against a labelled set. Dated is the share of matched facts with a gold date whose date also matches.</summary>
internal sealed record ExtractionScore(int TruePositives, int FalsePositives, int FalseNegatives, double Precision, double Recall, double F1, double Dated);

internal static class Scoring
{
    public static IReadOnlyList<LabelledEpisode> ReadJsonLines(string path) =>
        [.. File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize(line, BenchJson.Default.LabelledEpisode)
                ?? throw new InvalidDataException($"Not an episode: {line}"))];

    /// <summary>
    /// Matches facts by subject, predicate and object, ignoring case and surrounding space, within the same episode. An
    /// episode missing from the predictions counts all its gold facts as missed; one missing from the gold counts all
    /// its predictions as wrong.
    /// </summary>
    public static ExtractionScore Score(IReadOnlyList<LabelledEpisode> gold, IReadOnlyList<LabelledEpisode> predicted)
    {
        var predictions = predicted.ToDictionary(e => e.Episode, e => e.Facts, StringComparer.Ordinal);
        var goldIds = gold.Select(e => e.Episode).ToHashSet(StringComparer.Ordinal);
        int tp = 0, fn = 0, dated = 0, datedHit = 0;
        var fp = predicted.Where(e => !goldIds.Contains(e.Episode)).Sum(e => e.Facts.Count);

        foreach (var episode in gold)
        {
            var left = (predictions.GetValueOrDefault(episode.Episode) ?? []).ToList();
            foreach (var fact in episode.Facts)
            {
                var index = left.FindIndex(p => Key(p) == Key(fact));
                if (index < 0)
                {
                    fn++;
                    continue;
                }

                tp++;
                if (fact.ValidFrom is not null)
                {
                    dated++;
                    datedHit += left[index].ValidFrom == fact.ValidFrom ? 1 : 0;
                }

                left.RemoveAt(index);
            }

            fp += left.Count;
        }

        var precision = Ratio(tp, tp + fp);
        var recall = Ratio(tp, tp + fn);
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new ExtractionScore(tp, fp, fn, precision, recall, f1, Ratio(datedHit, dated));
    }

    private static string Key(LabelledFact fact) =>
        string.Create(CultureInfo.InvariantCulture, $"{Norm(fact.Subject)}|{Norm(fact.Predicate)}|{Norm(fact.Object)}");

    private static string Norm(string value) => value.Trim().ToUpperInvariant();

    private static double Ratio(int part, int whole) => whole == 0 ? 0 : (double)part / whole;
}
