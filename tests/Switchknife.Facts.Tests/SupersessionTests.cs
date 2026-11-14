namespace Switchknife.Facts.Tests;

/// <summary>Spec <c>docs/specs/2026-11-14-facts-supersession-intervals.md</c>, S1 to S6: closing a fact by naming its replacement.</summary>
public sealed class SupersessionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static async Task<(Harness Db, Guid Anna, Guid Tea, Guid Coffee)> WithTeaAndCoffeeAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        await db.Store.DefinePredicateAsync("likes", Cardinality.Many);
        await db.Store.DefinePredicateAsync("met", Cardinality.Event);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        db.At(1);
        await db.Store.IngestAsync(new NewEpisode("test", "a", "h1"), [new NewFact(anna, "likes", "tea", Harness.Day(0))]);
        db.At(10);
        await db.Store.IngestAsync(new NewEpisode("test", "b", "h2"), [new NewFact(anna, "likes", "coffee", Harness.Day(0))]);
        return (db, anna, await IdAsync(db, anna, "likes", "tea"), await IdAsync(db, anna, "likes", "coffee"));
    }

    private static async Task<Guid> IdAsync(Harness db, Guid subject, string predicate, string value) =>
        Assert.Single(await db.Store.HistoryAsync(subject, predicate), v => v.Fact.Value == value).Fact.Id;

    private static string[] Values(IEnumerable<Fact> facts) => [.. facts.Select(f => f.Value).Order()];

    [Fact]
    public async Task Believed_now_and_at_an_earlier_time_differ_after_a_supersession()
    {
        var (db, anna, tea, coffee) = await WithTeaAndCoffeeAsync(postgres);
        await using var _ = db;
        db.At(20);
        Assert.True(await db.Store.SupersedeAsync(tea, coffee));
        db.At(30);

        Assert.Equal(["coffee"], Values(await db.Store.CurrentAsync(anna, "likes")));
        Assert.Equal(["tea"], Values(await db.Store.AsOfAsync(anna, "likes", Harness.Day(5), Harness.Day(5))));
        Assert.Equal(["coffee", "tea"], Values(await db.Store.AsOfAsync(anna, "likes", Harness.Day(15), Harness.Day(15))));
        Assert.Equal(["coffee", "tea"], Values(await db.Store.AsOfAsync(anna, "likes", Harness.Day(15), Harness.Day(19))));
    }

    [Fact]
    public async Task A_superseded_fact_is_never_deleted_and_carries_its_successor_and_the_time()
    {
        var (db, anna, tea, coffee) = await WithTeaAndCoffeeAsync(postgres);
        await using var _ = db;
        var before = await db.CountAsync("facts");
        db.At(20);
        await db.Store.SupersedeAsync(tea, coffee);

        Assert.Equal(before, await db.CountAsync("facts"));
        var old = Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Id == tea);
        Assert.Equal("superseded", old.RetiredReason);
        Assert.Equal(coffee, old.SupersededBy);
        Assert.Equal(Harness.Day(20), old.RetiredAt);
        var current = Assert.Single(await db.Store.HistoryAsync(anna, "likes"), v => v.Fact.Id == coffee);
        Assert.Null(current.RetiredAt);
    }

    [Fact]
    public async Task Explain_shows_the_chain_in_both_directions()
    {
        var (db, _, tea, coffee) = await WithTeaAndCoffeeAsync(postgres);
        await using var _ = db;
        db.At(20);
        await db.Store.SupersedeAsync(tea, coffee);

        var old = await db.Store.ExplainAsync(tea);
        Assert.Equal([coffee], old!.Successors.Select(s => s.Fact.Id));
        var current = await db.Store.ExplainAsync(coffee);
        Assert.Equal([tea], current!.Predecessors.Select(p => p.Fact.Id));
    }

    [Fact]
    public async Task Search_skips_a_superseded_fact_unless_asked_about_an_earlier_belief()
    {
        var (db, _, tea, coffee) = await WithTeaAndCoffeeAsync(postgres);
        await using var _ = db;
        db.At(20);
        await db.Store.SupersedeAsync(tea, coffee);
        db.At(30);

        Assert.Empty(await db.Store.SearchAsync("tea", default, null));
        Assert.Equal(tea, Assert.Single(await db.Store.SearchAsync("tea", default, null, knownAt: Harness.Day(15))).Fact.Id);
    }

    [Fact]
    public async Task Superseding_twice_is_a_no_op_and_invalid_pairs_are_refused()
    {
        var (db, anna, tea, coffee) = await WithTeaAndCoffeeAsync(postgres);
        await using var _ = db;
        var bob = await db.Store.EnsureEntityAsync("person", "Bob");
        await db.Store.IngestAsync(new NewEpisode("test", "c", "h3"), [new NewFact(bob, "likes", "tea", Harness.Day(0)), new NewFact(anna, "met", "Bob", Harness.Day(2)), new NewFact(anna, "lives_in", "Kyiv", Harness.Day(0))]);
        var bobTea = await IdAsync(db, bob, "likes", "tea");
        var met = await IdAsync(db, anna, "met", "Bob");
        db.At(20);
        Assert.True(await db.Store.SupersedeAsync(tea, coffee));
        Assert.False(await db.Store.SupersedeAsync(tea, coffee));

        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.SupersedeAsync(coffee, coffee));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SupersedeAsync(tea, bobTea));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SupersedeAsync(coffee, tea));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SupersedeAsync(coffee, bobTea));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SupersedeAsync(met, coffee));
        var kyiv = Assert.Single(await db.Store.HistoryAsync(anna, "lives_in")).Fact.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SupersedeAsync(kyiv, coffee));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SupersedeAsync(coffee, kyiv));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SupersedeAsync(coffee, Guid.NewGuid()));
    }

    [Fact]
    public async Task Current_matches_a_rule_supersession_on_a_one_predicate()
    {
        await using var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("lives_in", Cardinality.One);
        var anna = await db.Store.EnsureEntityAsync("person", "Anna");
        db.At(1);
        await db.Store.IngestAsync(new NewEpisode("test", "a", "h1"), [new NewFact(anna, "lives_in", "Kyiv", Harness.Day(0))]);
        db.At(10);
        await db.Store.IngestAsync(new NewEpisode("test", "b", "h2"), [new NewFact(anna, "lives_in", "Lviv", Harness.Day(5))]);
        db.At(20);

        Assert.Equal(["Lviv"], Values(await db.Store.CurrentAsync(anna)));
        Assert.Equal(["Kyiv"], Values(await db.Store.AsOfAsync(anna, "lives_in", Harness.Day(3), Harness.Day(3))));
    }
}
