namespace Switchknife.Facts;

/// <summary>The source an ingest read. The same <paramref name="ContentHash"/> for one source is a no-op; a new hash is a new version.</summary>
/// <param name="SourceKind">What kind of source it is (<c>conversation</c>, <c>item</c>), chosen by the consumer.</param>
/// <param name="SourceRef">The consumer's id of the source.</param>
/// <param name="ContentHash">A hash of the source text; the text itself stays with the consumer.</param>
/// <param name="OccurredAt">When the source happened, for the record only; it never sets <c>recorded_at</c>. Defaults to the ingest time.</param>
public sealed record NewEpisode(string SourceKind, string SourceRef, string ContentHash, DateTimeOffset? OccurredAt = null);

/// <summary>A fact to write.</summary>
/// <param name="Subject">The entity it is about (<see cref="FactsStore.EnsureEntityAsync"/>).</param>
/// <param name="Predicate">A predicate defined with <see cref="FactsStore.DefinePredicateAsync"/>.</param>
/// <param name="Value">The object of the fact, as text.</param>
/// <param name="ValidFrom">When it began to be true in the world.</param>
/// <param name="ValidTo">When it stopped, for <see cref="Cardinality.Many"/> only; null means unknown, not forever.</param>
/// <param name="Text">The rendered sentence, at most 300 characters; defaults to the predicate and the value.</param>
/// <param name="LocatorJson">A JSON object pointing into the episode (segment ids, a line range) that the consumer's resolver understands.</param>
/// <param name="Quote">An optional short quote from the source.</param>
public sealed record NewFact(
    Guid Subject,
    string Predicate,
    string Value,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo = null,
    string? Text = null,
    string LocatorJson = "{}",
    string? Quote = null);

/// <summary>What an ingest did.</summary>
/// <param name="EpisodeId">The episode's id; for a duplicate, the one already stored.</param>
/// <param name="Duplicate">The episode was already stored with this hash, so nothing was written.</param>
/// <param name="Written">Facts that became new rows.</param>
/// <param name="Corroborated">Facts that were already known and only gained a source.</param>
public sealed record IngestResult(Guid EpisodeId, bool Duplicate, int Written, int Corroborated);

/// <summary>A fact as believed at some instant.</summary>
/// <param name="Id">The row's id.</param>
/// <param name="Subject">The entity it is about.</param>
/// <param name="Predicate">Its predicate.</param>
/// <param name="Value">Its value.</param>
/// <param name="Text">Its rendered sentence.</param>
/// <param name="ValidFrom">When it began to be true.</param>
/// <param name="ValidTo">When it stopped; null when unknown.</param>
/// <param name="RecordedAt">When the store learned it.</param>
public sealed record Fact(Guid Id, Guid Subject, string Predicate, string Value, string Text, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, DateTimeOffset RecordedAt);

/// <summary>One stored row with both timelines, live or retired.</summary>
/// <param name="Fact">The row.</param>
/// <param name="RetiredAt">When the store stopped believing it; null while it is live.</param>
/// <param name="RetiredReason">Why: <c>superseded</c>, <c>retracted</c>, <c>source_changed</c> or <c>merged</c>.</param>
/// <param name="SupersededBy">The row that replaced it, when one did.</param>
public sealed record FactVersion(Fact Fact, DateTimeOffset? RetiredAt, string? RetiredReason, Guid? SupersededBy);
