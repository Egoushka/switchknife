using System.Text.Json.Serialization;

namespace Switchknife.Facts.Extraction;

/// <summary>
/// What an extractor returns for one episode (wire format v1). Every property is optional on the way in, so a model that
/// leaves one out gets a rejection from the <see cref="ExtractionValidator"/> instead of a parse failure; <see cref="ExtractionSchema"/>
/// asks providers for all of them.
/// </summary>
/// <param name="Facts">The extracted facts.</param>
public sealed record ExtractedEpisode(IReadOnlyList<ExtractedFact?>? Facts);

/// <summary>An entity as the extractor names it: a handle from the context, or a name and kind for something not listed.</summary>
/// <param name="Ref">A handle of the context (<c>e1</c>), or null.</param>
/// <param name="Name">The name as written in the source, when there is no handle.</param>
/// <param name="Kind">What it is (<c>person</c>, <c>place</c>, <c>organisation</c>), when there is no handle.</param>
public sealed record ExtractedMention(string? Ref, string? Name, string? Kind);

/// <summary>One fact as the extractor wrote it, before validation.</summary>
/// <param name="Subject">The entity it is about.</param>
/// <param name="Predicate">A listed predicate, or <c>statement</c>.</param>
/// <param name="Value">The object as text, for a listed predicate; exactly one of this and <paramref name="ObjectEntity"/>.</param>
/// <param name="ObjectEntity">The object as an entity (wire name <c>object</c>).</param>
/// <param name="Text">The fact as one sentence, at most 300 characters.</param>
/// <param name="ValidFrom">When it began, <c>YYYY-MM-DD</c> or ISO 8601 with an offset; null when the source does not say.</param>
/// <param name="ValidTo">When it ended, in the same formats.</param>
/// <param name="Replaces">A handle of a live fact (<c>f1</c>) this one makes outdated.</param>
/// <param name="Evidence">The ids of the source lines it comes from.</param>
/// <param name="Quote">A short quote copied from one of those lines.</param>
/// <param name="Salience">0 to 1: how much it matters; orders facts when the cap bites.</param>
public sealed record ExtractedFact(
    ExtractedMention? Subject,
    string? Predicate,
    string? Value,
    [property: JsonPropertyName("object")] ExtractedMention? ObjectEntity,
    string? Text,
    string? ValidFrom,
    string? ValidTo,
    string? Replaces,
    IReadOnlyList<string>? Evidence,
    string? Quote,
    double? Salience);

/// <summary>Source-generated serialization of the extraction types, so nothing reflects (Native AOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ExtractedEpisode))]
public sealed partial class ExtractionJsonContext : JsonSerializerContext;
