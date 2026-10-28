using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Tests.Extraction;

public sealed class ValidatorTests
{
    [Fact]
    public void A_valid_fact_is_accepted_with_resolved_handles_dates_and_locator()
    {
        var result = Fx.Validate(Fx.Lives with { Evidence = ["l1", "l2", "l1"] });

        Assert.Empty(result.Rejections);
        var fact = Assert.Single(result.Facts);
        Assert.Equal(new EntityMention("person", "Anna", Fx.Anna), fact.Subject);
        Assert.Equal(("lives_in", Cardinality.One, "Kyiv", "Anna lives in Kyiv."), (fact.Predicate, fact.Cardinality, fact.Value, fact.Text));
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), fact.ValidFrom);
        Assert.False(fact.StartDefaulted);
        Assert.Null(fact.ValidTo);
        Assert.Equal("""{"evidence":["l1","l2"]}""", fact.LocatorJson);
        Assert.Equal("moved to Kyiv", fact.Quote);
        Assert.Equal(0.9, fact.Salience);
    }

    [Fact]
    public void A_missing_start_date_becomes_the_episode_time_and_says_so()
    {
        var defaulted = Assert.Single(Fx.Validate(Fx.Likes).Facts);
        Assert.Equal(Fx.At, defaulted.ValidFrom);
        Assert.True(defaulted.StartDefaulted);

        var given = Assert.Single(Fx.Validate(Fx.Likes with { ValidFrom = "2026-05-02T08:30:00+03:00" }).Facts);
        Assert.Equal(new DateTimeOffset(2026, 5, 2, 5, 30, 0, TimeSpan.Zero), given.ValidFrom);
        Assert.False(given.StartDefaulted);
    }

    [Theory]
    [InlineData("1899-12-31", null, RejectReason.DateOutOfRange)]
    [InlineData("2028-10-27", null, RejectReason.DateOutOfRange)]
    [InlineData("2026-01-01", "1899-06-01", RejectReason.DateOutOfRange)]
    [InlineData("2026-01-01", "2029-01-01", RejectReason.DateOutOfRange)]
    [InlineData("2026-03-01", "2026-03-01", RejectReason.EndBeforeStart)]
    [InlineData("2026-03-01", "2026-02-01", RejectReason.EndBeforeStart)]
    [InlineData(null, "2026-01-01", RejectReason.EndBeforeStart)]
    [InlineData("last March", null, RejectReason.BadDate)]
    [InlineData("2026-13-45", null, RejectReason.BadDate)]
    [InlineData("2026-03-01T10:00:00", null, RejectReason.BadDate)]
    [InlineData("2026-03-01", "soon", RejectReason.BadDate)]
    public void A_date_before_1900_or_past_the_horizon_or_ending_before_it_begins_rejects_the_fact(string? from, string? to, RejectReason expected)
    {
        var result = Fx.Validate(Fx.Likes with { ValidFrom = from, ValidTo = to }, new ExtractionOptions());

        Assert.Empty(result.Facts);
        Assert.Equal([new Rejection(0, expected)], result.Rejections);
    }

    [Fact]
    public void The_horizon_includes_its_last_day_and_the_other_facts_survive_a_bad_one()
    {
        // 2026-10-27T10:00 + 730 days = 2028-10-26T10:00, so 2028-10-26 midnight is inside.
        var result = Fx.Validate(new ExtractionOptions(), Fx.Likes with { ValidFrom = "2028-10-26" }, Fx.Likes with { ValidFrom = "2028-10-27" }, Fx.Lives);

        Assert.Equal([0, 2], result.Facts.Select(f => f.Index));
        Assert.Equal([new Rejection(1, RejectReason.DateOutOfRange)], result.Rejections);
    }

    [Fact]
    public void Evidence_outside_the_episode_or_missing_rejects_the_fact()
    {
        Assert.Equal([new Rejection(0, RejectReason.EvidenceOutsideEpisode)], Fx.Validate(Fx.Likes with { Evidence = ["l2", "l99"] }).Rejections);
        Assert.Equal([new Rejection(0, RejectReason.NoEvidence)], Fx.Validate(Fx.Likes with { Evidence = [] }).Rejections);
        Assert.Equal([new Rejection(0, RejectReason.NoEvidence)], Fx.Validate(Fx.Likes with { Evidence = null }).Rejections);
        Assert.Equal([new Rejection(0, RejectReason.NoEvidence)], Fx.Validate(Fx.Likes with { Evidence = [" ", ""] }).Rejections);
    }

    [Fact]
    public void A_quote_not_in_the_cited_lines_is_dropped_and_the_fact_kept()
    {
        string? QuoteOf(string quote, params string[] evidence)
        {
            var result = Fx.Validate(Fx.Likes with { Quote = quote, Evidence = evidence });
            return Assert.Single(result.Facts).Quote;
        }

        Assert.Null(QuoteOf("works at Acme", "l2"));
        Assert.Null(QuoteOf("a thing nobody said", "l2"));
        Assert.Null(QuoteOf(new string('x', 201), "l2"));
        Assert.Equal("LOVE jazz, especially", QuoteOf("LOVE jazz, especially", "l2"));
        Assert.Equal("works at Acme", QuoteOf("works at Acme", "l2", "l3"));

        var dropped = Fx.Validate(Fx.Likes with { Quote = "nope" });
        Assert.Equal(1, dropped.QuotesDropped);
        Assert.Equal(0, Fx.Validate(Fx.Likes with { Quote = "live" }).QuotesDropped);
    }

    [Fact]
    public void A_replaces_that_is_not_a_live_fact_of_the_same_subject_is_dropped_and_the_fact_kept()
    {
        Guid? Kept(ExtractedFact fact, out int dropped)
        {
            var result = Fx.Validate(fact);
            dropped = result.ReplacesDropped;
            return Assert.Single(result.Facts).Replaces;
        }

        Assert.Null(Kept(Fx.Likes with { Replaces = "f99" }, out var unknown));
        Assert.Null(Kept(Fx.Likes with { Replaces = "f2" }, out var otherSubject));
        Assert.Null(Kept(Fx.Lives with { Replaces = "f3" }, out var oneByRule));
        Assert.Null(Kept(Fx.Likes with { Predicate = "met", Replaces = "f1" }, out var eventNever));
        Assert.Equal((1, 1, 1, 1), (unknown, otherSubject, oneByRule, eventNever));

        Assert.Equal(Fx.AnnaLikesTea, Kept(Fx.Likes with { Replaces = "f1" }, out var good));
        Assert.Equal(0, good);
        Assert.Equal(Fx.AnnaLikesTea, Kept(Fx.Likes with { Predicate = "statement", Value = null, Replaces = " f1 " }, out _));

        // The subject given by exact name instead of a handle matches the same way.
        var byName = Fx.Likes with { Subject = new ExtractedMention(null, "  anna ", "Person"), Replaces = "f1" };
        Assert.Equal(Fx.AnnaLikesTea, Kept(byName, out _));
        var stranger = Fx.Likes with { Subject = new ExtractedMention(null, "Carol", "person"), Replaces = "f1" };
        Assert.Null(Kept(stranger, out var strangerDropped));
        Assert.Equal(1, strangerDropped);
    }

    [Fact]
    public void An_unknown_predicate_becomes_a_statement_unless_the_option_rejects_it()
    {
        var odd = Fx.Likes with { Predicate = "adores", Value = "ignored", Text = "Anna adores jazz." };

        var downgraded = Fx.Validate(odd);
        var fact = Assert.Single(downgraded.Facts);
        Assert.Equal(("statement", Cardinality.Many, "Anna adores jazz.", true), (fact.Predicate, fact.Cardinality, fact.Value, fact.Downgraded));
        Assert.Equal(1, downgraded.Downgraded);

        // A model that wrote an unlisted predicate and also an object is not penalised: the sentence carries the object.
        Assert.Single(Fx.Validate(odd with { ObjectEntity = new ExtractedMention("e2", null, null) }).Facts);

        Assert.Equal([new Rejection(0, RejectReason.UnknownPredicate)], Fx.Validate(odd, new ExtractionOptions { UnknownPredicates = UnknownPredicateMode.Reject }).Rejections);

        var typed = Assert.Single(Fx.Validate(Fx.Likes).Facts);
        Assert.Equal(("likes", false), (typed.Predicate, typed.Downgraded));

        var explicitStatement = Fx.Likes with { Predicate = "statement", Value = null };
        var statement = Assert.Single(Fx.Validate(explicitStatement).Facts);
        Assert.Equal(("statement", "Anna likes jazz.", false), (statement.Predicate, statement.Value, statement.Downgraded));
        Assert.Equal([new Rejection(0, RejectReason.BadObject)], Fx.Validate(explicitStatement with { ObjectEntity = new ExtractedMention("e2", null, null) }).Rejections);
    }

    [Fact]
    public void Facts_beyond_the_cap_are_rejected_least_salient_first()
    {
        ExtractedFact Fact(int n, double? salience) => Fx.Likes with { Value = $"thing {n}", Text = $"Anna likes thing {n}.", Salience = salience };
        var options = new ExtractionOptions { MaxFacts = 5 };

        var result = Fx.Validate(options, Fact(0, 0.9), Fact(1, null), Fact(2, 0.2), Fact(3, 0.9), Fact(4, 0.2), Fact(5, 0.5), Fact(6, 0.7), Fact(7, 0.1));

        Assert.Equal([0, 2, 3, 5, 6], result.Facts.Select(f => f.Index));
        Assert.Equal(
            [new Rejection(1, RejectReason.OverCap), new Rejection(4, RejectReason.OverCap), new Rejection(7, RejectReason.OverCap)],
            result.Rejections);
    }

    [Fact]
    public void A_fact_needs_exactly_one_object_and_known_or_named_entities()
    {
        var both = Fx.Validate(Fx.Likes with { ObjectEntity = new ExtractedMention("e2", null, null) });
        var neither = Fx.Validate(Fx.Likes with { Value = " " });
        var unknownRef = Fx.Validate(Fx.Likes with { Subject = new ExtractedMention("e9", null, null) });
        var nameless = Fx.Validate(Fx.Likes with { Subject = new ExtractedMention(null, "Carol", null) });
        var noSubject = Fx.Validate(Fx.Likes with { Subject = null });
        var badObject = Fx.Validate(Fx.Likes with { Value = null, ObjectEntity = new ExtractedMention("e7", null, null) });

        Assert.Equal(RejectReason.BadObject, Assert.Single(both.Rejections).Reason);
        Assert.Equal(RejectReason.BadObject, Assert.Single(neither.Rejections).Reason);
        Assert.Equal(RejectReason.UnknownEntity, Assert.Single(unknownRef.Rejections).Reason);
        Assert.Equal(RejectReason.BadEntity, Assert.Single(nameless.Rejections).Reason);
        Assert.Equal(RejectReason.BadEntity, Assert.Single(noSubject.Rejections).Reason);
        Assert.Equal(RejectReason.UnknownEntity, Assert.Single(badObject.Rejections).Reason);

        var named = Assert.Single(Fx.Validate(Fx.Likes with { Subject = new ExtractedMention(null, "Carol", "person") }).Facts);
        Assert.Equal(new EntityMention("person", "Carol", null), named.Subject);

        var entityObject = Assert.Single(Fx.Validate(Fx.Likes with { Predicate = "works_at", Value = null, Text = "Anna works with Bob.", ObjectEntity = new ExtractedMention("e2", null, null) }).Facts);
        Assert.Equal(new EntityMention("person", "Bob", Fx.Bob), entityObject.ObjectEntity);
        Assert.Null(entityObject.Value);
    }

    [Fact]
    public void A_repeated_fact_in_one_output_is_kept_once()
    {
        var result = Fx.Validate(new ExtractionOptions(), Fx.Likes, Fx.Likes with { Value = "  JAZZ ", Text = "Anna likes JAZZ." }, Fx.Likes with { ValidFrom = "2026-01-01" });

        Assert.Equal([0, 2], result.Facts.Select(f => f.Index));
        Assert.Equal([new Rejection(1, RejectReason.Duplicate)], result.Rejections);
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(0, false)]
    public void Text_that_is_blank_or_over_300_characters_rejects_the_fact(int length, bool accepted)
    {
        var result = Fx.Validate(Fx.Likes with { Text = new string('a', length) });
        Assert.Equal(accepted, result.Facts.Count == 1);
        Assert.Equal(accepted ? [] : [new Rejection(0, RejectReason.BadText)], result.Rejections);
        Assert.Equal([new Rejection(0, RejectReason.BadText)], Fx.Validate(Fx.Likes with { Text = "   " }).Rejections);
        Assert.Equal([new Rejection(0, RejectReason.BadPredicate)], Fx.Validate(Fx.Likes with { Predicate = " " }).Rejections);
    }

    [Fact]
    public void A_rejection_carries_an_index_and_a_code_and_no_text()
    {
        Assert.All(typeof(Rejection).GetProperties(), p => Assert.True(p.PropertyType == typeof(int) || p.PropertyType == typeof(RejectReason), p.Name));

        const string secret = "Anna told me her passcode is 4417";
        var bad = Fx.Likes with { Text = secret, Evidence = ["nope"], Quote = secret };
        var result = Fx.Validate(new ExtractionOptions(), bad, bad, bad);
        Assert.Equal(3, result.Rejections.Count);
        var report = string.Join(' ', result.Rejections.Select(r => r.ToString())) + result.ToString();
        Assert.DoesNotContain("4417", report, StringComparison.Ordinal);
        Assert.DoesNotContain("passcode", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_validator_refuses_a_cap_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fx.Validate(new ExtractionOptions { MaxFacts = 0 }, Fx.Likes));
    }
}
