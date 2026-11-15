using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Testing.Gold;

/// <summary>One fact a gold item expects.</summary>
/// <param name="Kind">The predicate it should come out under.</param>
/// <param name="Match">Anchor groups: a fact matches when every group has an alternative inside the emitted fact's text.</param>
/// <param name="Claim">The fact in words, shown when it is missed.</param>
/// <param name="Deadline">The <c>YYYY-MM-DD</c> the fact should end on, when the source gives one.</param>
/// <param name="KindAlternatives">Other predicates that also count as the right kind.</param>
public sealed record GoldFact(
    string Kind,
    IReadOnlyList<IReadOnlyList<string>> Match,
    string Claim = "",
    string? Deadline = null,
    IReadOnlyList<string>? KindAlternatives = null);

/// <summary>One episode of the gold set; an item with no facts is a negative, and any fact emitted for it is wrong.</summary>
public sealed record GoldItem(string Id, IReadOnlyList<GoldFact> Facts)
{
    /// <summary>Nothing collectable in the source.</summary>
    public bool Negative => Facts.Count == 0;
}

/// <summary>A gold item with the context the extractor is given for it.</summary>
public sealed record GoldCase(GoldItem Item, ExtractionContext Context);

/// <summary>A fact as an extractor emitted it; only the parts the scorer reads.</summary>
public sealed record EmittedFact(
    string? Subject,
    string? Predicate,
    string? Text,
    string? Value,
    string? ObjectName,
    DateTimeOffset? ValidTo,
    double? Salience);

/// <summary>What an extractor gave for one item: its facts, or the reason it gave none.</summary>
public sealed record ItemResult(IReadOnlyList<EmittedFact> Facts, string? Error = null);

/// <summary>How one item scored.</summary>
public sealed record ItemScore(string Id, bool Negative, int Expected, int Matched, int Need, bool? Recalled, int Emitted, IReadOnlyList<string> Missed, string? Error);

/// <summary>A scored run. Fact recall counts positive items only; precision is strict, because the gold lists headline facts, not every true statement.</summary>
public sealed record GoldScore(
    int ItemsRecalled,
    int ItemsTotal,
    int FactsMatched,
    int FactsExpected,
    int Emitted,
    int EmittedMatched,
    double? StrictPrecision,
    int NegativesClean,
    int NegativesTotal,
    int KindMatched,
    int KindTotal,
    int DeadlineMatched,
    int DeadlineTotal,
    IReadOnlyList<ItemScore> Rows);
