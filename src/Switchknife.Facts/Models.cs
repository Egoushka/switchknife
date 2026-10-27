namespace Switchknife.Facts;

/// <summary>How far an episode's source is believed. A fact is as trusted as its most trusted source; a lower-trust episode never retires a higher-trust fact.</summary>
public enum EpisodeTrust
{
    /// <summary>A stranger's speech, a web page: it may add facts and corroborate, never override.</summary>
    ThirdParty = 0,

    /// <summary>Another program or agent.</summary>
    Agent = 1,

    /// <summary>The owner's own words and notes. The default.</summary>
    Owner = 2,
}

/// <summary>The source an ingest read. The same <paramref name="ContentHash"/> for one source is a no-op; a new hash is a new version that makes the earlier ones stale.</summary>
/// <param name="SourceKind">What kind of source it is (<c>conversation</c>, <c>item</c>), chosen by the consumer.</param>
/// <param name="SourceRef">The consumer's id of the source.</param>
/// <param name="ContentHash">A hash of the source text; the text itself stays with the consumer.</param>
/// <param name="OccurredAt">When the source happened, for the record only; it never sets <c>recorded_at</c>. Defaults to the ingest time.</param>
/// <param name="Trust">How far the source is believed.</param>
public sealed record NewEpisode(
    string SourceKind, string SourceRef, string ContentHash, DateTimeOffset? OccurredAt = null, EpisodeTrust Trust = EpisodeTrust.Owner);

/// <summary>A fact to write.</summary>
/// <param name="Subject">The entity it is about (<see cref="FactsStore.EnsureEntityAsync"/>).</param>
/// <param name="Predicate">A predicate defined with <see cref="FactsStore.DefinePredicateAsync"/>.</param>
/// <param name="Value">The object of the fact, as text; null when the object is an entity (<paramref name="ObjectEntity"/>).</param>
/// <param name="ValidFrom">When it began to be true in the world.</param>
/// <param name="ValidTo">When it stopped; null means unknown, not forever. For a <see cref="Cardinality.One"/> predicate the value before it resumes afterwards.</param>
/// <param name="Text">The rendered sentence, at most 300 characters; defaults to the predicate and the value.</param>
/// <param name="LocatorJson">A JSON object pointing into the episode (segment ids, a line range) that the consumer's resolver understands.</param>
/// <param name="Quote">An optional short quote from the source.</param>
/// <param name="ObjectEntity">The entity the fact points at, instead of <paramref name="Value"/>; exactly one of the two is given, and <paramref name="Text"/> is required with an entity.</param>
public sealed record NewFact(
    Guid Subject,
    string Predicate,
    string? Value,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo = null,
    string? Text = null,
    string LocatorJson = "{}",
    string? Quote = null,
    Guid? ObjectEntity = null);

/// <summary>What an ingest did.</summary>
/// <param name="EpisodeId">The episode's id; for a duplicate, the one already stored.</param>
/// <param name="Duplicate">The episode was already stored with this hash, so nothing was written.</param>
/// <param name="Written">Facts that became new rows.</param>
/// <param name="Corroborated">Facts that were already known and only gained a source.</param>
/// <param name="Outranked">Facts stored already retired because a higher-trust fact holds the slot.</param>
public sealed record IngestResult(Guid EpisodeId, bool Duplicate, int Written, int Corroborated, int Outranked = 0);

/// <summary>A fact as believed at some instant.</summary>
/// <param name="Id">The row's id.</param>
/// <param name="Subject">The entity it is about.</param>
/// <param name="Predicate">Its predicate.</param>
/// <param name="Value">Its value; empty when the object is an entity.</param>
/// <param name="Text">Its rendered sentence.</param>
/// <param name="ValidFrom">When it began to be true.</param>
/// <param name="ValidTo">When it stopped; null when unknown.</param>
/// <param name="RecordedAt">When the store learned it.</param>
/// <param name="ObjectEntity">The entity it points at, or null for a text value.</param>
public sealed record Fact(
    Guid Id, Guid Subject, string Predicate, string Value, string Text, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, DateTimeOffset RecordedAt, Guid? ObjectEntity = null);

/// <summary>One stored row with both timelines, live or retired.</summary>
/// <param name="Fact">The row.</param>
/// <param name="RetiredAt">When the store stopped believing it; null while it is live.</param>
/// <param name="RetiredReason">Why: <c>superseded</c>, <c>retracted</c>, <c>source_changed</c>, <c>outranked</c> or <c>merged</c>.</param>
/// <param name="SupersededBy">The row that replaced it, when one did.</param>
public sealed record FactVersion(Fact Fact, DateTimeOffset? RetiredAt, string? RetiredReason, Guid? SupersededBy);
