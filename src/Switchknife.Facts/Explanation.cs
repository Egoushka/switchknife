namespace Switchknife.Facts;

/// <summary>One place a fact was read: a source line of the episode that asserted it.</summary>
/// <param name="EpisodeId">The episode that read it.</param>
/// <param name="SourceKind">What kind of source it is, as the consumer named it.</param>
/// <param name="SourceRef">The consumer's id of the source.</param>
/// <param name="ContentHash">The version of the source the episode read.</param>
/// <param name="LocatorJson">The JSON object pointing into the episode (segment ids, a line range) that the consumer's resolver understands.</param>
/// <param name="Quote">The short quote kept with the line, if any.</param>
/// <param name="Trust">How far the episode's source is believed.</param>
/// <param name="OccurredAt">When the source happened, as the ingest said.</param>
/// <param name="RecordedAt">When the store read the episode.</param>
/// <param name="Extractor">The extractor that produced the episode's facts (for the kit, <c>kit/1+&lt;prompt hash&gt;</c>); null when written by plain <c>IngestAsync</c>.</param>
/// <param name="Model">The model the extractor used; null when unknown.</param>
public sealed record FactSource(
    Guid EpisodeId, string SourceKind, string SourceRef, string ContentHash, string LocatorJson, string? Quote, EpisodeTrust Trust, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt,
    string? Extractor = null, string? Model = null);

/// <summary>The latest retraction of a fact's assertion.</summary>
/// <param name="RetractedAt">When it was retracted.</param>
/// <param name="RestoredAt">When it was restored; null while the retraction still blocks the assertion.</param>
public sealed record FactRetraction(DateTimeOffset RetractedAt, DateTimeOffset? RestoredAt);

/// <summary>Why a stored row is believed, from where, and what became of it. See <see cref="FactsStore.ExplainAsync"/>.</summary>
/// <param name="Version">The row with both timelines: validity range, when it was recorded, and when and why it was retired.</param>
/// <param name="Sources">Every source line, oldest episode first. Empty only for a row written by raw SQL.</param>
/// <param name="Trust">The highest trust among the sources (the one the write path compares); the lowest when there are none.</param>
/// <param name="Retraction">The latest retraction of the row's assertion, or null if it was never retracted.</param>
/// <param name="Successors">The rows that replaced this one, nearest first: its successor, that row's successor, and so on (at most 32).</param>
/// <param name="Predecessors">The rows that this one replaced, oldest first.</param>
public sealed record FactExplanation(
    FactVersion Version,
    IReadOnlyList<FactSource> Sources,
    EpisodeTrust Trust,
    FactRetraction? Retraction,
    IReadOnlyList<FactVersion> Successors,
    IReadOnlyList<FactVersion> Predecessors);
