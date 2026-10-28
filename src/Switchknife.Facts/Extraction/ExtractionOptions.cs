namespace Switchknife.Facts.Extraction;

/// <summary>What to do with a predicate that is not listed in the context.</summary>
public enum UnknownPredicateMode
{
    /// <summary>Keep the fact as an open <c>statement</c>: predicate <c>statement</c>, value = its text.</summary>
    Statement,

    /// <summary>Reject the fact.</summary>
    Reject,
}

/// <summary>Limits for validation and the rules the prompt states. Part of the prompt hash.</summary>
public sealed record ExtractionOptions
{
    /// <summary>Facts accepted per episode (Nytka uses 5); the least salient beyond it are rejected.</summary>
    public int MaxFacts { get; init; } = 20;

    /// <summary>A date later than the episode time plus this many days is rejected (Nytka: 730).</summary>
    public int HorizonDays { get; init; } = 730;

    /// <summary>A quote longer than this is dropped.</summary>
    public int MaxQuoteLength { get; init; } = 200;

    /// <summary>What happens to a predicate the context does not list.</summary>
    public UnknownPredicateMode UnknownPredicates { get; init; } = UnknownPredicateMode.Statement;

    /// <summary>Rules of the consumer's own, appended to the prompt (and so to its hash).</summary>
    public IReadOnlyList<string> ExtraRules { get; init; } = [];
}
