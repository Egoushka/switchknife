using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Tests.Extraction;

/// <summary>A small episode the extraction tests share: Anna and Bob, three live facts, four predicates, three lines.</summary>
internal static class Fx
{
    public static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    public static readonly Guid Anna = new("00000000-0000-0000-0000-00000000a001");
    public static readonly Guid Bob = new("00000000-0000-0000-0000-00000000b002");
    public static readonly Guid AnnaLikesTea = new("00000000-0000-0000-0000-0000000000f1");
    public static readonly Guid BobLikesCoffee = new("00000000-0000-0000-0000-0000000000f2");
    public static readonly Guid AnnaLivesInLviv = new("00000000-0000-0000-0000-0000000000f3");

    public static ExtractionContext Context(ContextBudget? budget = null) => new(
        At,
        [new EvidenceLine("l1", "I moved to Kyiv last March.", "Anna"), new EvidenceLine("l2", "I love  jazz,\nespecially live.", "Anna"), new EvidenceLine("l3", "Bob works at Acme now.", "Anna")],
        [new PredicateSpec("lives_in", Cardinality.One, "where a person lives"), new PredicateSpec("works_at", Cardinality.One), new PredicateSpec("likes", Cardinality.Many, "something a person enjoys"), new PredicateSpec("met", Cardinality.Event)],
        [new KnownEntity(Anna, "person", "Anna"), new KnownEntity(Bob, "person", "Bob")],
        [new KnownFact(AnnaLikesTea, Anna, "likes", "Anna likes tea."), new KnownFact(BobLikesCoffee, Bob, "likes", "Bob likes coffee."), new KnownFact(AnnaLivesInLviv, Anna, "lives_in", "Anna lives in Lviv.")],
        budget);

    public static ExtractedMention AnnaRef => new("e1", null, null);

    /// <summary>A fact that passes every rule: Anna likes jazz, from line l2.</summary>
    public static ExtractedFact Likes => new(AnnaRef, "likes", "jazz", null, "Anna likes jazz.", null, null, null, ["l2"], null, 0.5);

    /// <summary>A typed single-valued fact with a date: Anna lives in Kyiv since March, from line l1.</summary>
    public static ExtractedFact Lives => new(AnnaRef, "lives_in", "Kyiv", null, "Anna lives in Kyiv.", "2026-03-01", null, null, ["l1"], "moved to Kyiv", 0.9);

    public static ExtractedEpisode Output(params ExtractedFact[] facts) => new(facts);

    public static ValidatedExtraction Validate(ExtractedFact fact, ExtractionOptions? options = null) => ExtractionValidator.Validate(Output(fact), Context(), options);

    public static ValidatedExtraction Validate(ExtractionOptions options, params ExtractedFact[] facts) => ExtractionValidator.Validate(Output(facts), Context(), options);
}
