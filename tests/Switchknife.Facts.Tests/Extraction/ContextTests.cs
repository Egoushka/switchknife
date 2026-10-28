using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Tests.Extraction;

public sealed class ContextTests
{
    [Fact]
    public void Context_is_cut_to_its_budget_in_the_given_order_and_reports_the_cut()
    {
        var entities = Enumerable.Range(0, 3).Select(i => new KnownEntity(new Guid(i + 1, 0, 0, new byte[8]), "person", $"P{i}")).ToList();
        var facts = Enumerable.Range(0, 250).Select(i => new KnownFact(new Guid(1000 + i, 0, 0, new byte[8]), entities[0].Id, "likes", $"P0 likes {i}.")).ToList();

        var context = new ExtractionContext(Fx.At, [], [], entities, facts);

        Assert.Equal(200, context.Facts.Count);
        Assert.Equal(50, context.CutFacts);
        Assert.Equal(("f1", "f200"), (context.Facts[0].Handle, context.Facts[^1].Handle));
        Assert.Equal(facts[0].Id, context.Facts[0].Fact.Id);
        Assert.True(context.TryResolveFact("f200", out var last));
        Assert.Equal(facts[199].Id, last.Id);
        Assert.False(context.TryResolveFact("f201", out _));
        Assert.False(context.TryResolveFact("f0", out _));
        Assert.False(context.TryResolveFact(null, out _));

        var small = new ExtractionContext(Fx.At, [], [], entities, facts, new ContextBudget(MaxEntities: 2, MaxFacts: 5));
        Assert.Equal(["e1", "e2"], small.Entities.Select(e => e.Handle));
        Assert.Equal(1, small.CutEntities);
        Assert.Equal(5, small.Facts.Count);
        Assert.False(small.TryResolveEntity("e3", out _));
    }

    [Fact]
    public void A_fact_whose_entity_was_cut_is_cut_with_it()
    {
        var anna = new KnownEntity(Fx.Anna, "person", "Anna");
        var bob = new KnownEntity(Fx.Bob, "person", "Bob");
        var context = new ExtractionContext(
            Fx.At, [], [], [anna, bob],
            [new KnownFact(Fx.BobLikesCoffee, Fx.Bob, "likes", "Bob likes coffee."), new KnownFact(Fx.AnnaLikesTea, Fx.Anna, "likes", "Anna likes tea.")],
            new ContextBudget(MaxEntities: 1));

        var kept = Assert.Single(context.Facts);
        Assert.Equal((Fx.AnnaLikesTea, "f1"), (kept.Fact.Id, kept.Handle));
        Assert.Equal(1, context.CutFacts);
    }

    [Fact]
    public void Context_rejects_duplicate_or_malformed_evidence_ids_and_duplicate_predicates()
    {
        Assert.Throws<ArgumentException>(() => new ExtractionContext(Fx.At, [new EvidenceLine("a", "x"), new EvidenceLine("a", "y")], []));
        Assert.Throws<ArgumentException>(() => new ExtractionContext(Fx.At, [new EvidenceLine("", "x")], []));
        Assert.Throws<ArgumentException>(() => new ExtractionContext(Fx.At, [new EvidenceLine("a b", "x")], []));
        Assert.Throws<ArgumentException>(() => new ExtractionContext(Fx.At, [new EvidenceLine("a\n[l2]", "x")], []));
        Assert.Throws<ArgumentException>(() => new ExtractionContext(Fx.At, [], [new PredicateSpec("likes", Cardinality.Many), new PredicateSpec("likes", Cardinality.One)]));
    }

    [Fact]
    public void An_entity_found_by_name_must_be_unique_in_the_context()
    {
        var context = new ExtractionContext(
            Fx.At, [], [],
            [new KnownEntity(Fx.Anna, "person", "Anna"), new KnownEntity(Fx.Bob, "person", "Anna"), new KnownEntity(Guid.NewGuid(), "place", "Kyiv")]);

        Assert.Null(context.FindEntity("person", "Anna"));
        Assert.NotNull(context.FindEntity("PLACE", " kyiv "));
        Assert.Null(context.FindEntity("place", "Lviv"));
    }

    [Fact]
    public void The_statement_predicate_is_never_listed_even_if_the_consumer_lists_it()
    {
        var context = new ExtractionContext(Fx.At, [], [new PredicateSpec("statement", Cardinality.One), new PredicateSpec("likes", Cardinality.Many)]);
        Assert.Equal(["likes"], context.Predicates.Select(p => p.Name));
        Assert.False(context.TryGetPredicate("statement", out _));
    }
}
