using Switchknife.Facts.Bench;
using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Bench.Tests;

public sealed class LabellingTests
{
    private static readonly DateTimeOffset Monday = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_transcript_becomes_numbered_lines_with_speakers_and_continuations_join_their_line()
    {
        var conversation = new NytkaConversation("c1", Monday, "[09:00:01] Wearer: I moved to Lviv.\nlast week, in fact\n[09:00:05] Voice 2: Nice.\n\n");

        var episode = Labelling.FromNytka("n-001", conversation);

        Assert.Equal(("n-001", "nytka", "c1", Monday), (episode.Episode, episode.Source, episode.Ref, episode.OccurredAt));
        Assert.Equal(
            [new CandidateLine("l1", "I moved to Lviv. last week, in fact", "Wearer"), new CandidateLine("l2", "Nice.", "Voice 2")],
            episode.Lines);
    }

    [Fact]
    public void Windows_keep_lines_whole_stay_within_the_budget_and_number_from_one()
    {
        var lines = Enumerable.Range(1, 10).Select(i => new CandidateLine("", new string((char)('a' + i), 30))).ToList();

        var first = Labelling.Window(lines, 1, 100);
        var second = Labelling.Window(lines, 2, 100);
        var oversized = Labelling.Window([new CandidateLine("", new string('x', 250))], 1, 100);

        Assert.Equal(3, first.Count);
        Assert.Equal(["l1", "l2", "l3"], second.Select(l => l.Id));
        Assert.Equal(lines[3].Text, second[0].Text);
        Assert.Equal(100, Assert.Single(oversized).Text.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => Labelling.Window(lines, 5, 100));
    }

    [Fact]
    public void A_feed_item_is_its_feed_and_title_then_one_line_per_paragraph()
    {
        var item = new FeedItem(42, "1.24.0", Monday, "What's new\n\n Exit nodes \r\n", "Release notes from pangolin");

        var episode = Labelling.FromFeed("f-001", item);
        var untitled = Labelling.FromFeed("f-002", new FeedItem(43, null, Monday, "Body"));

        Assert.Equal(("feed", "42"), (episode.Source, episode.Ref));
        Assert.Equal(["Release notes from pangolin", "1.24.0", "What's new", "Exit nodes"], episode.Lines.Select(l => l.Text));
        Assert.Equal(["Body"], untitled.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_planted_copy_changes_every_line_is_marked_and_refuses_a_replacement_that_matches_nothing()
    {
        var original = Episode("n-001", ("l1", "I live in Lviv."), ("l2", "Lviv is cold."));

        var planted = Labelling.Plant(original, "n-001p", Monday.AddDays(30), [("Lviv", "Kyiv")]);

        Assert.Equal(["I live in Kyiv.", "Kyiv is cold."], planted.Lines.Select(l => l.Text));
        Assert.Equal((true, "n-001", Monday.AddDays(30)), (planted.Planted, planted.PlantedFrom, planted.OccurredAt));
        Assert.Null(original.Planted);
        Assert.Throws<ArgumentException>(() => Labelling.Plant(original, "x", Monday, [("Odesa", "Kyiv")]));
    }

    [Fact]
    public void A_draft_keeps_accepted_facts_as_triples_with_given_dates_only()
    {
        var episode = Episode("n-001", ("l1", "I moved to Lviv in March."), ("l2", "Anna works at a bank."));
        const string output = """
            Here it is:
            ```json
            {"facts":[
              {"subject":{"ref":null,"name":"owner","kind":"person"},"predicate":"lives_in","value":"Lviv","object":null,"text":"The owner lives in Lviv.","validFrom":"2026-03-01","validTo":null,"replaces":null,"evidence":["l1"],"quote":null,"salience":0.9},
              {"subject":{"ref":null,"name":"Anna","kind":"person"},"predicate":"works_at","value":null,"object":{"ref":null,"name":"a bank","kind":"organization"},"text":"Anna works at a bank.","validFrom":null,"validTo":null,"replaces":null,"evidence":["l2"],"quote":null,"salience":0.5},
              {"subject":{"ref":null,"name":"Anna","kind":"person"},"predicate":"likes","value":"tea","object":null,"text":"Anna likes tea.","validFrom":null,"validTo":null,"replaces":null,"evidence":["l9"],"quote":null,"salience":0.1}
            ]}
            ```
            """;

        var (draft, validation, failure) = Labelling.Draft(episode, output);

        Assert.Null(failure);
        Assert.Equal([new LabelledFact("owner", "lives_in", "Lviv", "2026-03-01"), new LabelledFact("Anna", "works_at", "a bank")], draft.Facts);
        Assert.Equal(RejectReason.EvidenceOutsideEpisode, Assert.Single(validation!.Rejections).Reason);
        Assert.Equal("nytka", draft.Source);
    }

    [Fact]
    public void An_output_that_does_not_parse_is_an_empty_draft_with_the_failure()
    {
        var (draft, validation, failure) = Labelling.Draft(Episode("n-001", ("l1", "x")), "no json here");

        Assert.Empty(draft.Facts);
        Assert.Null(validation);
        Assert.Equal(ParseFailure.BadJson, failure);
    }

    [Fact]
    public void Gold_files_round_trip_with_edit_source_and_planted_marks_and_score_themselves_perfectly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gold-{Guid.NewGuid():N}.jsonl");
        try
        {
            LabelledEpisode[] gold =
            [
                new("n-001", [new("owner", "lives_in", "Lviv", "2026-03-01") { Edited = true }, new("Anna", "works_at", "a bank")]) { Source = "nytka", Planted = true },
                new("f-001", [new("Pangolin", "latest_version", "1.24.0")]) { Source = "feed" },
            ];

            Labelling.WriteGold(path, gold);
            var read = Scoring.ReadJsonLines(path);
            var text = File.ReadAllText(path);

            Assert.Equal(gold.Length, read.Count);
            Assert.Equal(gold[0].Facts, read[0].Facts);
            Assert.Equal((true, "nytka"), (read[0].Planted, read[0].Source));
            Assert.Null(read[1].Planted);
            Assert.Equal(1, text.Split("\"edited\"").Length - 1);
            Assert.Equal(2, File.ReadAllLines(path).Length);
            Assert.Equal(1.0, Scoring.Score(read, read).F1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Episodes_are_added_or_replaced_by_id_and_read_back_equal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"episodes-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = Episode("n-001", ("l1", "one"));
            var second = Episode("n-002", ("l1", "two"));

            Labelling.WriteEpisodes(path, [first, second]);
            Labelling.WriteEpisodes(path, [first with { Ref = "changed" }]);
            var read = Labelling.ReadEpisodes(path);

            Assert.Equal(["n-001", "n-002"], read.Select(e => e.Episode));
            Assert.Equal("changed", read[0].Ref);
            Assert.Equal(second.Lines, read[1].Lines);
            Assert.Empty(Labelling.ReadEpisodes(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pairs_are_changes_of_a_one_at_a_time_value_in_time_order()
    {
        CandidateEpisode[] episodes =
        [
            Episode("n-005", ("l1", "x")),
            Episode("n-002", ("l1", "x")) with { OccurredAt = Monday.AddDays(60) },
            Episode("n-003", ("l1", "x")) with { OccurredAt = Monday.AddDays(90), Planted = true },
        ];
        LabelledEpisode[] gold =
        [
            new("n-002", [new("owner", "lives_in", "Kyiv", "2026-04-01"), new("owner", "likes", "coffee")]),
            new("n-005", [new("Owner", "lives_in", "Lviv"), new("owner", "likes", "tea")]),
            new("n-003", [new("owner", "LIVES_IN", "kyiv "), new("owner", "works_at", "Acme")]),
        ];

        var pairs = Labelling.Pairs(gold, episodes);

        var pair = Assert.Single(pairs);
        Assert.Equal(("owner", "lives_in"), (pair.Subject, pair.Predicate));
        Assert.Equal(new PairSide("n-005", "Lviv", "2026-03-02"), pair.Earlier);
        Assert.Equal(new PairSide("n-002", "Kyiv", "2026-04-01"), pair.Later);
        Assert.False(pair.Planted);

        var planted = Labelling.Pairs([.. gold, new("n-004", [new("owner", "lives_in", "Odesa", "2026-07-01")]) { Planted = true }], episodes);
        Assert.Equal(2, planted.Count);
        Assert.True(planted[1].Planted);
    }

    [Fact]
    public void Review_edits_are_marked_and_removed_facts_counted()
    {
        LabelledEpisode[] draft = [new("n-001", [new("owner", "lives_in", "Lviv"), new("owner", "likes", "tea"), new("Anna", "owns", "a cat")])];
        LabelledEpisode[] reviewed = [new("n-001", [new("Owner", "lives_in", "lviv "), new("owner", "likes", "green tea"), new("owner", "uses", "Rider", "2026-01-01")])];

        var (gold, removed) = Labelling.MarkEdits(draft, reviewed);

        Assert.Equal([null, true, true], gold[0].Facts.Select(f => f.Edited));
        Assert.Equal(2, removed);
        var (dated, _) = Labelling.MarkEdits(draft, [new("n-001", [new("owner", "lives_in", "Lviv", "2026-02-01")])]);
        Assert.True(dated[0].Facts[0].Edited);
    }

    [Fact]
    public void Check_counts_the_set_and_names_each_problem_without_text()
    {
        CandidateEpisode[] episodes = [Episode("n-001", ("l1", "secret words")), Episode("f-001", ("l1", "x")) with { Source = "feed", Planted = true }];
        LabelledEpisode[] gold =
        [
            new("n-001", [new("owner", "lives_in", "Lviv") { Edited = true }, new("owner", "feels", "fine"), new("owner", "likes", "tea", "March")]),
            new("f-001", [new("Pangolin", "latest_version", "1.24.0")]),
            new("n-009", []),
            new("f-001", []),
        ];

        var report = Labelling.Check(gold, episodes);

        Assert.Equal((4, 1, 2, 2, 4, 1, 0, 0), (report.Episodes, report.NytkaEpisodes, report.FeedEpisodes, report.PlantedEpisodes, report.Facts, report.EditedFacts, report.Pairs, report.PlantedPairs));
        Assert.Contains("f-001: appears 2 times", report.Problems);
        Assert.Contains("n-009: not in the episodes file", report.Problems);
        Assert.Contains("n-001 fact 2: predicate not in the catalog", report.Problems);
        Assert.Contains("n-001 fact 3: from is not YYYY-MM-DD", report.Problems);
        Assert.Contains(report.Problems, p => p.StartsWith("mix:", StringComparison.Ordinal));
        Assert.Contains(report.Problems, p => p.StartsWith("pairs:", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Problems, p => p.Contains("secret", StringComparison.Ordinal) || p.Contains("Lviv", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_episode_gets_the_kits_prompt_with_its_lines_and_one_extractor_name()
    {
        var (first, name) = Labelling.PromptDocument(Episode("n-001", ("l1", "I moved to Lviv.")));
        var (second, other) = Labelling.PromptDocument(Episode("f-001", ("l1", "Release 1.24.0")));

        Assert.Equal(name, other);
        Assert.StartsWith("kit/1+", name, StringComparison.Ordinal);
        Assert.Contains("[l1] Wearer: I moved to Lviv.", first, StringComparison.Ordinal);
        Assert.Contains("latest_version | one", second, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\"", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Change_cues_rank_candidates()
    {
        Assert.Equal(2, Labelling.Cues(Episode("n-001", ("l1", "I moved, and now I live elsewhere."))));
        Assert.Equal(0, Labelling.Cues(Episode("n-002", ("l1", "Nice weather."))));
    }

    private static CandidateEpisode Episode(string id, params (string Id, string Text)[] lines) =>
        new(id, "nytka", "ref-" + id, Monday, [.. lines.Select(l => new CandidateLine(l.Id, l.Text, "Wearer"))]);
}
