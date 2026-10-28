using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Switchknife.Facts.Extraction;

/// <summary>Why an extractor's text could not be read.</summary>
public enum ParseFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>No JSON object could be parsed from the text (empty, truncated, prose only).</summary>
    BadJson,

    /// <summary>JSON was found, but it is not an <see cref="ExtractedEpisode"/> (no <c>facts</c> list, a field of the wrong type, a null fact).</summary>
    BadShape,
}

/// <summary>Reads the text a model returned. Tolerant of what models wrap around JSON, strict about the JSON itself.</summary>
public static class ExtractionParser
{
    /// <summary>
    /// Parses <paramref name="text"/>: bare JSON, JSON inside a markdown code fence, or JSON with a sentence before or after it
    /// (the outermost <c>{…}</c>). Unknown properties are ignored. Never throws for bad input.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ExtractedEpisode? episode, out ParseFailure failure)
    {
        episode = null;
        var start = text?.IndexOf('{', StringComparison.Ordinal) ?? -1;
        var end = text?.LastIndexOf('}') ?? -1;
        if (text is null || start < 0 || end <= start)
        {
            failure = ParseFailure.BadJson;
            return false;
        }

        var json = text.AsSpan(start, end - start + 1);
        try
        {
            using var document = JsonDocument.Parse(json.ToString());
        }
        catch (JsonException)
        {
            failure = ParseFailure.BadJson;
            return false;
        }

        try
        {
            episode = JsonSerializer.Deserialize(json, ExtractionJsonContext.Default.ExtractedEpisode);
        }
        catch (JsonException)
        {
            episode = null;
        }

        if (episode?.Facts is null || episode.Facts.Any(f => f is null))
        {
            episode = null;
            failure = ParseFailure.BadShape;
            return false;
        }

        failure = ParseFailure.None;
        return true;
    }
}
