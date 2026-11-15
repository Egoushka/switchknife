using Switchknife.Facts.Extraction;
using Switchknife.Facts.Testing.Gold;

namespace Switchknife.Facts.Testing.Tests;

public sealed class GoldSetTests
{
    private static GoldFact Fact(string kind, string claim, params string[][] match) => new(kind, match, claim);

    private static readonly GoldItem Prices = new("prices",
    [
        Fact("price_change", "Luna costs 0.10", ["luna"], ["0.10"]),
        Fact("price_change", "Opus is 20% cheaper", ["opus 5.5"], ["20%", "4/"]),
    ]);

    private static readonly GoldItem Opinion = new("opinion", []);

    private static EmittedFact Emit(string subject, string text, string predicate = "price_change", string? value = null, DateTimeOffset? validTo = null, double? salience = null) =>
        new(subject, predicate, text, value, null, validTo, salience);

    private static Dictionary<string, ItemResult> Results(params (string Id, ItemResult Result)[] items) =>
        items.ToDictionary(i => i.Id, i => i.Result);

    private static ItemResult Facts(params EmittedFact[] facts) => new(facts, null);

    [Fact]
    public void Gold_claims_fed_back_score_full_marks()
    {
        var score = GoldScorer.Score([Prices, Opinion], Results(("prices", Facts(
            Emit("GPT-6 Luna", "Luna costs $0.10 per million"),
            Emit("Claude", "Opus 5.5 is 20% cheaper")))));

        Assert.Equal((1, 1, 2, 2), (score.ItemsRecalled, score.ItemsTotal, score.FactsMatched, score.FactsExpected));
        Assert.Equal(1.0, score.StrictPrecision);
        Assert.Equal((1, 1), (score.NegativesClean, score.NegativesTotal));
    }

    [Fact]
    public void Empty_results_recall_nothing_and_leave_precision_undefined()
    {
        var score = GoldScorer.Score([Prices, Opinion], Results());

        Assert.Equal(0, score.ItemsRecalled);
        Assert.Null(score.StrictPrecision);
        Assert.Equal(1, score.NegativesClean);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void An_item_is_recalled_when_half_its_facts_match(int matched, bool recalled)
    {
        var emitted = matched == 1 ? [Emit("x", "luna 0.10")] : Array.Empty<EmittedFact>();

        var row = Assert.Single(GoldScorer.Score([Prices], Results(("prices", Facts(emitted)))).Rows);

        Assert.Equal((matched, 1, recalled), (row.Matched, row.Need, row.Recalled));
        Assert.Equal(matched == 1 ? ["Opus is 20% cheaper"] : ["Luna costs 0.10", "Opus is 20% cheaper"], row.Missed);
    }

    [Fact]
    public void One_emitted_fact_serves_one_expected_fact()
    {
        var item = new GoldItem("twins", [Fact("idea", "a", ["foo"]), Fact("idea", "b", ["foo"])]);

        var score = GoldScorer.Score([item], Results(("twins", Facts(Emit("s", "foo")))));

        Assert.Equal(1, score.FactsMatched);
    }

    [Fact]
    public void A_negative_item_with_an_emitted_fact_is_not_clean()
    {
        var score = GoldScorer.Score([Opinion], Results(("opinion", Facts(Emit("s", "anything")))));

        Assert.Equal((0, 1), (score.NegativesClean, score.NegativesTotal));
        Assert.Equal(1, score.Emitted);
    }

    [Fact]
    public void Kind_and_deadline_are_scored_on_matched_facts_without_blocking_the_match()
    {
        var item = new GoldItem("gig", [new GoldFact("opportunity", [["rust"]], "Rust gig", "2026-12-01", ["bounty"])]);

        var score = GoldScorer.Score([item], Results(("gig", Facts(
            Emit("s", "rust gig", "idea", validTo: new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero))))));

        Assert.Equal(1, score.FactsMatched);
        Assert.Equal((0, 1, 1, 1), (score.KindMatched, score.KindTotal, score.DeadlineMatched, score.DeadlineTotal));
    }

    [Fact]
    public void An_alternative_kind_counts_as_right()
    {
        var item = new GoldItem("gig", [new GoldFact("opportunity", [["rust"]], "Rust gig", null, ["bounty"])]);

        var score = GoldScorer.Score([item], Results(("gig", Facts(Emit("s", "rust gig", "bounty")))));

        Assert.Equal((1, 1), (score.KindMatched, score.KindTotal));
    }

    [Fact]
    public void Facts_under_the_salience_floor_are_not_counted()
    {
        var score = GoldScorer.Score([Prices], Results(("prices", Facts(
            Emit("s", "luna 0.10", salience: 0.2),
            Emit("s", "opus 5.5 20%", salience: 0.9)))), minSalience: 0.5);

        Assert.Equal((1, 1), (score.Emitted, score.FactsMatched));
    }

    [Fact]
    public void A_triple_shaped_gold_maps_to_one_group_per_part()
    {
        var item = new GoldItem("e1", [Fact("likes", "Anna likes jazz", ["anna"], ["likes"], ["jazz"])]);

        var score = GoldScorer.Score([item], Results(("e1", Facts(Emit("Anna", "Anna likes jazz.", "likes", "jazz")))));

        Assert.Equal(1, score.ItemsRecalled);
    }

    private static ExtractionContext ContextFor(string line) => new(
        new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero),
        [new EvidenceLine("l1", line)],
        [new PredicateSpec("price_change", Cardinality.Many)],
        []);

    private static string Reply(string subject, string text) =>
        $$"""{"facts":[{"subject":{"ref":null,"name":"{{subject}}","kind":"product"},"predicate":"price_change","value":"0.10","object":null,"text":"{{text}}","validFrom":null,"validTo":null,"replaces":null,"evidence":["l1"],"quote":null,"salience":0.8}]}""";

    [Fact]
    public async Task The_runner_scores_a_stub_extractor_and_keeps_going_after_a_bad_item()
    {
        var extractor = new ScriptedExtractor(
            Reply("Luna", "Luna costs 0.10 per million."),
            new TimeoutException("model down"),
            "not json at all",
            "{\"facts\":[]}");
        GoldCase[] cases =
        [
            new(new GoldItem("a", [Fact("price_change", "Luna", ["luna"])]), ContextFor("Luna costs 0.10")),
            new(new GoldItem("b", [Fact("price_change", "Opus", ["opus"])]), ContextFor("Opus is cheaper")),
            new(new GoldItem("c", [Fact("price_change", "Sol", ["sol"])]), ContextFor("Sol rose")),
            new(new GoldItem("d", []), ContextFor("I think so")),
        ];

        var score = await GoldRunner.RunAsync(extractor, cases);

        Assert.Equal((1, 3, 1), (score.ItemsRecalled, score.ItemsTotal, score.NegativesClean));
        Assert.Equal(["a", "b", "c", "d"], score.Rows.Select(r => r.Id));
        Assert.Null(score.Rows[0].Error);
        Assert.Equal(ExtractionFailures.ExtractorError, score.Rows[1].Error);
        Assert.Equal(ExtractionFailures.BadJson, score.Rows[2].Error);
        Assert.Equal(4, extractor.Calls);
    }

    [Fact]
    public async Task The_runner_lets_cancellation_through()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var extractor = new ScriptedExtractor(token => Task.FromCanceled<ExtractorResponse>(token));
        GoldCase[] cases = [new(Opinion, ContextFor("x"))];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoldRunner.RunAsync(extractor, cases, cancellationToken: source.Token));
    }
}
