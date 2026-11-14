namespace Switchknife.Facts.Tests;

/// <summary>Spec <c>docs/specs/2026-11-14-facts-supersession-intervals.md</c>, I1 to I5: relations between validity windows.</summary>
public sealed class IntervalTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // The query window is days 10 to 20. Each fact is named for the relation its own window [from, to) has to that one.
    private static readonly (string Name, int From, int? To)[] Windows =
    [
        ("before", 0, 5), ("meets", 2, 10), ("overlaps", 5, 15), ("starts", 10, 15), ("during", 12, 18), ("finishes", 15, 20),
        ("equals", 10, 20), ("after", 25, 30), ("metby", 20, 25), ("overlappedby", 15, 25), ("startedby", 10, 25),
        ("contains", 5, 25), ("finishedby", 5, 20),
    ];

    private static async Task<Harness> SeededAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("holds", Cardinality.Many);
        var subject = await db.Store.EnsureEntityAsync("thing", "Box");
        db.At(1);
        await db.Store.IngestAsync(
            new NewEpisode("test", "a", "h1"),
            [.. Windows.Select(w => new NewFact(subject, "holds", w.Name, Harness.Day(w.From), w.To is { } to ? Harness.Day(to) : null))]);
        db.At(2);
        return db;
    }

    private static string[] Names(IEnumerable<Fact> facts) => [.. facts.Select(f => f.Value).Order()];

    public static TheoryData<IntervalRelation, string[]> Expected => new()
    {
        { IntervalRelation.Before, ["before"] },
        { IntervalRelation.Meets, ["meets"] },
        { IntervalRelation.Overlaps, ["overlaps"] },
        { IntervalRelation.Starts, ["starts"] },
        { IntervalRelation.During, ["during"] },
        { IntervalRelation.Finishes, ["finishes"] },
        { IntervalRelation.Equals, ["equals"] },
        { IntervalRelation.After, ["after"] },
        { IntervalRelation.MetBy, ["metby"] },
        { IntervalRelation.OverlappedBy, ["overlappedby"] },
        { IntervalRelation.StartedBy, ["startedby"] },
        { IntervalRelation.Contains, ["contains"] },
        { IntervalRelation.FinishedBy, ["finishedby"] },
        {
            IntervalRelation.Intersects,
            ["contains", "during", "equals", "finishedby", "finishes", "overlappedby", "overlaps", "startedby", "starts"]
        },
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task Each_relation_returns_exactly_its_rows(IntervalRelation relation, string[] expected)
    {
        await using var db = await SeededAsync(postgres);
        Assert.Equal(expected, Names(await db.Store.QueryAsync(relation, Harness.Day(10), Harness.Day(20))));
    }

    [Fact]
    public async Task An_open_end_is_infinite()
    {
        await using var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("holds", Cardinality.Many);
        var subject = await db.Store.EnsureEntityAsync("thing", "Box");
        db.At(1);
        await db.Store.IngestAsync(
            new NewEpisode("test", "a", "h1"),
            [new NewFact(subject, "holds", "open", Harness.Day(0)), new NewFact(subject, "holds", "bounded", Harness.Day(30), Harness.Day(40))]);
        db.At(2);

        Assert.Equal(["open"], Names(await db.Store.QueryAsync(IntervalRelation.Contains, Harness.Day(30), Harness.Day(40))));
        Assert.Equal(["open"], Names(await db.Store.QueryAsync(IntervalRelation.Intersects, Harness.Day(100), null)));
        Assert.Equal(["bounded", "open"], Names(await db.Store.QueryAsync(IntervalRelation.Intersects, null, null)));
        Assert.Equal(["open"], Names(await db.Store.QueryAsync(IntervalRelation.StartedBy, Harness.Day(0), Harness.Day(10))));
    }

    [Fact]
    public async Task While_returns_the_facts_that_overlap_one_fact_and_not_itself()
    {
        await using var db = await SeededAsync(postgres);
        var equals = (await db.Store.QueryAsync(IntervalRelation.Equals, Harness.Day(10), Harness.Day(20))).Single().Id;

        var overlapping = Names(await db.Store.WhileAsync(equals));
        Assert.DoesNotContain("equals", overlapping);
        Assert.Equal(["contains", "during", "finishedby", "finishes", "overlappedby", "overlaps", "startedby", "starts"], overlapping);
        Assert.Equal(["contains"], Names(await db.Store.WhileAsync(equals, IntervalRelation.Contains)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.WhileAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Results_equal_hand_written_range_sql()
    {
        await using var db = await SeededAsync(postgres);
        var queries = new (IntervalRelation Relation, string Sql)[]
        {
            (IntervalRelation.Intersects, "valid && tstzrange('2026-01-11', '2026-01-21', '[)')"),
            (IntervalRelation.During, "valid <@ tstzrange('2026-01-11', '2026-01-21', '[)') and valid <> tstzrange('2026-01-11', '2026-01-21', '[)') and lower(valid) > '2026-01-11' and upper(valid) < '2026-01-21'"),
            (IntervalRelation.Meets, "upper(valid) = '2026-01-11'"),
        };
        foreach (var (relation, sql) in queries)
        {
            await using var command = db.Data.CreateCommand($"select text from facts.facts where {sql} order by text");
            await using var reader = await command.ExecuteReaderAsync();
            var hand = new List<string>();
            while (await reader.ReadAsync())
            {
                hand.Add(reader.GetString(0));
            }

            var library = (await db.Store.QueryAsync(relation, Harness.Day(10), Harness.Day(20))).Select(f => f.Text).Order().ToList();
            Assert.Equal(hand, library);
        }
    }

    [Fact]
    public async Task An_event_is_an_instant_for_intersects_and_works_as_a_window()
    {
        await using var db = await postgres.NewAsync();
        await db.Store.DefinePredicateAsync("met", Cardinality.Event);
        await db.Store.DefinePredicateAsync("holds", Cardinality.Many);
        var subject = await db.Store.EnsureEntityAsync("thing", "Box");
        db.At(1);
        await db.Store.IngestAsync(
            new NewEpisode("test", "a", "h1"),
            [new NewFact(subject, "met", "Bob", Harness.Day(10)), new NewFact(subject, "holds", "wide", Harness.Day(5), Harness.Day(15))]);
        db.At(2);

        Assert.Equal(["Bob"], Names(await db.Store.QueryAsync(IntervalRelation.Intersects, Harness.Day(10), Harness.Day(20), predicate: "met")));
        Assert.Empty(await db.Store.QueryAsync(IntervalRelation.Intersects, Harness.Day(11), Harness.Day(20), predicate: "met"));
        var met = (await db.Store.QueryAsync(IntervalRelation.Intersects, null, null, predicate: "met")).Single().Id;
        Assert.Equal(["wide"], Names(await db.Store.WhileAsync(met)));
    }

    [Fact]
    public async Task Belief_time_applies_and_a_future_instant_is_refused()
    {
        await using var db = await SeededAsync(postgres);
        var during = (await db.Store.QueryAsync(IntervalRelation.During, Harness.Day(10), Harness.Day(20))).Single().Id;
        db.At(5);
        await db.Store.RetractAsync(during);
        db.At(6);

        Assert.Empty(await db.Store.QueryAsync(IntervalRelation.During, Harness.Day(10), Harness.Day(20)));
        Assert.Equal(["during"], Names(await db.Store.QueryAsync(IntervalRelation.During, Harness.Day(10), Harness.Day(20), knownAt: Harness.Day(3))));
        Assert.Empty(await db.Store.QueryAsync(IntervalRelation.During, Harness.Day(10), Harness.Day(20), knownAt: Harness.Day(0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store.QueryAsync(IntervalRelation.During, Harness.Day(10), Harness.Day(20), knownAt: Harness.Day(7)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Store.QueryAsync(IntervalRelation.During, Harness.Day(20), Harness.Day(10)));
    }
}
