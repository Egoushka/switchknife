using System.Text.Json;
using Switchknife.Facts;
using Switchknife.Knowledge.Approvals;
using Switchknife.Knowledge.Ideas;

namespace Switchknife.Knowledge.Tests;

/// <summary>Spec <c>docs/specs/2026-11-01-knowledge-k3-ideas.md</c>, checks I22-I24.</summary>
public sealed class KnowledgeApprovalsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly Guid IdeaId = Guid.Parse("7f3c1a52-0000-4000-8000-000000000002");
    private static readonly Guid ExperimentId = Guid.Parse("7f3c1a52-0000-4000-8000-000000000001");
    private static readonly ItemDelivery Delivery = new("https://hooks.example/knowledge-decision");

    private static readonly CheapestTest Test = new("landing page", CapHours: 6m, CapUah: 0m, "signups", "10 in 14 days", TimeBoxDays: 14);

    private static readonly IdeaProposal Proposal = new(
        IdeaId, "Tool that grades ideas", ValueKind.Money, new ValueHypothesis("freelancers", "save 2 h a week", "3 interviews", 5000m), Test);

    private static ApprovalDecision Decide(ApprovalItem item, string? action, Dictionary<string, string>? edited = null) =>
        new("01JBTESTDECISION0000000001", item.Id, action, edited ?? [], "2026-01-21T00:00:00Z", null);

    [Fact]
    public void I22_idea_item_shows_seed_and_editable_draft()
    {
        var item = KnowledgeApprovals.IdeaItem(Proposal, Delivery);

        Assert.Equal($"knowledge:idea:{IdeaId}", item.Id);
        Assert.Equal(("knowledge", "decision"), (item.Source, item.Kind));
        Assert.Equal("Tool that grades ideas", item.Body["seed"]);
        Assert.Equal(("money", "freelancers", "5000", "10 in 14 days", "14"), (item.Body["value_kind"], item.Body["who"], item.Body["expected_uah_per_month"], item.Body["pass_line"], item.Body["time_box_days"]));
        Assert.DoesNotContain("body.seed", item.Editable);
        Assert.All(item.Editable, path => Assert.True(item.Body.ContainsKey(path["body.".Length..])));
        Assert.Contains("body.who", item.Editable);
        Assert.Contains("body.pass_line", item.Editable);
        Assert.Contains("body.kill_reason", item.Editable);
        Assert.Equal(["start_test", "keep_seed", "kill"], item.Actions.Select(a => a.Id));
        Assert.Equal(new ApprovalAction("kill", "Kill", "danger", true), item.Actions[2]);

        var json = JsonSerializer.Serialize(item, ApprovalJsonContext.Default.ApprovalItem);
        Assert.Contains("\"editable\":[\"body.value_kind\"", json);
        Assert.Contains("\"time_box_days\":\"14\"", json);
        Assert.DoesNotContain("expires_at", json);
    }

    [Fact]
    public void I22_start_test_parses_into_a_validated_draft_with_the_edits()
    {
        var item = KnowledgeApprovals.IdeaItem(Proposal, Delivery);

        var parsed = KnowledgeApprovals.ParseIdea(item, Decide(item, "start_test", new() { ["body.pass_line"] = "12 in 14 days", ["body.cap_hours"] = "4.5" }));

        Assert.True(parsed.Accepted);
        Assert.Equal((IdeaId, IdeaChoice.StartTest), (parsed.Idea!.Value, parsed.Choice!.Value));
        Assert.Equal(new IdeaDraft(IdeaStatus.Testing, ValueKind.Money, Proposal.Hypothesis, Test with { PassLine = "12 in 14 days", CapHours = 4.5m }), parsed.Draft);
    }

    [Fact]
    public void I22_a_seed_without_a_draft_gets_the_validator_codes()
    {
        var item = KnowledgeApprovals.IdeaItem(new IdeaProposal(IdeaId, "Bare seed"), Delivery);

        var parsed = KnowledgeApprovals.ParseIdea(item, Decide(item, "start_test", new() { ["body.cap_hours"] = "4", ["body.cap_uah"] = "0", ["body.value_kind"] = "money" }));

        Assert.Null(parsed.Draft);
        Assert.Contains(new DecisionRejection("missing", "body.who"), parsed.Rejections);
        Assert.Contains(new DecisionRejection("missing", "body.test_action"), parsed.Rejections);
        Assert.Contains(new DecisionRejection("missing", "body.pass_line"), parsed.Rejections);
        Assert.Contains(new DecisionRejection("not_positive", "body.expected_uah_per_month"), parsed.Rejections);
        Assert.Equal(
            [new DecisionRejection("missing", "body.cap_hours"), new DecisionRejection("missing", "body.cap_uah")],
            KnowledgeApprovals.ParseIdea(item, Decide(item, "start_test")).Rejections);
    }

    [Fact]
    public void I22_kill_needs_a_reason_and_keep_seed_needs_nothing()
    {
        var item = KnowledgeApprovals.IdeaItem(Proposal, Delivery);

        Assert.Equal([new DecisionRejection("missing", "body.kill_reason")], KnowledgeApprovals.ParseIdea(item, Decide(item, "kill")).Rejections);
        Assert.Equal(
            new IdeaDraft(IdeaStatus.Killed, KillReason: "no buyers"),
            KnowledgeApprovals.ParseIdea(item, Decide(item, "kill", new() { ["body.kill_reason"] = " no buyers " })).Draft);
        var keep = KnowledgeApprovals.ParseIdea(item, Decide(item, "keep_seed"));
        Assert.True(keep.Accepted);
        Assert.Equal((IdeaChoice.KeepSeed, (IdeaDraft?)null), (keep.Choice!.Value, keep.Draft));
    }

    [Fact]
    public void I23_result_item_shows_the_pass_line_and_takes_metric_uah_note()
    {
        var item = KnowledgeApprovals.ResultItem(new ResultProposal(ExperimentId, Test), Delivery);

        Assert.Equal($"knowledge:result:{ExperimentId}", item.Id);
        Assert.Equal("10 in 14 days", item.Body["pass_line"]);
        Assert.Equal(["passed", "failed", "inconclusive"], item.Actions.Select(a => a.Id));
        Assert.Equal(["body.metric_value", "body.uah", "body.note"], item.Editable);

        var parsed = KnowledgeApprovals.ParseResult(item, Decide(item, "passed", new() { ["body.metric_value"] = "14", ["body.uah"] = "3000", ["body.note"] = "first client" }));

        Assert.True(parsed.Accepted);
        Assert.Equal(
            (ExperimentId, ResultOutcome.Passed, 14m, 3000m, "first client", Harness.Day(20)),
            (parsed.Experiment!.Value, parsed.Outcome!.Value, parsed.MetricValue!.Value, parsed.Uah!.Value, parsed.Note, parsed.At!.Value));
    }

    public static TheoryData<string, string?, string?, string, string, string> Untrusted => new()
    {
        { "result", "knowledge:result:7f3c1a52-0000-4000-8000-000000000001", "merge", "{}", "invalid", "action" },
        { "result", "knowledge:result:7f3c1a52-0000-4000-8000-000000000001", null, "{}", "missing", "action" },
        { "result", "knowledge:result:7f3c1a52-0000-4000-8000-000000000001", "passed", """{"body.uah":"-500"}""", "negative", "body.uah" },
        { "result", "knowledge:result:7f3c1a52-0000-4000-8000-000000000001", "passed", """{"body.uah":"lots"}""", "invalid", "body.uah" },
        { "result", "knowledge:result:7f3c1a52-0000-4000-8000-000000000001", "passed", """{"body.pass_line":"1"}""", "not_editable", "body.pass_line" },
        { "result", null, "passed", "{}", "missing", "id" },
        { "result", "knowledge:result:00000000-0000-4000-8000-000000000009", "passed", "{}", "invalid", "id" },
        { "idea", "knowledge:idea:7f3c1a52-0000-4000-8000-000000000002", "promote", "{}", "invalid", "action" },
        { "idea", "knowledge:idea:7f3c1a52-0000-4000-8000-000000000002", "kill", """{"body.seed":"other"}""", "not_editable", "body.seed" },
        { "idea", "knowledge:idea:7f3c1a52-0000-4000-8000-000000000002", "start_test", """{"body.cap_hours":"6 h"}""", "invalid", "body.cap_hours" },
        { "idea", null, "keep_seed", "{}", "missing", "id" },
    };

    [Theory]
    [MemberData(nameof(Untrusted))]
    public void I24_untrusted_decisions_are_refused_with_a_code(string itemKind, string? id, string? action, string edited, string reason, string field)
    {
        var item = itemKind == "idea" ? KnowledgeApprovals.IdeaItem(Proposal, Delivery) : KnowledgeApprovals.ResultItem(new ResultProposal(ExperimentId, Test), Delivery);
        var decision = new ApprovalDecision(
            "01JBTESTDECISION0000000001", id, action, JsonSerializer.Deserialize(edited, ApprovalJsonContext.Default.DictionaryStringString), "2026-01-21T00:00:00Z", null);

        var rejections = itemKind == "idea" ? KnowledgeApprovals.ParseIdea(item, decision).Rejections : KnowledgeApprovals.ParseResult(item, decision).Rejections;

        Assert.Contains(new DecisionRejection(reason, field), rejections);
    }

    [Fact]
    public void I24_a_decision_without_its_id_or_time_or_json_is_refused()
    {
        var item = KnowledgeApprovals.ResultItem(new ResultProposal(ExperimentId, Test), Delivery);

        var parsed = KnowledgeApprovals.ParseResult(item, new ApprovalDecision(null, item.Id, "failed", null, "yesterday", null));

        Assert.Equal([new DecisionRejection("missing", "decision"), new DecisionRejection("invalid", "at")], parsed.Rejections);
        Assert.Null(KnowledgeApprovals.ReadDecision("not json"));
        Assert.Null(KnowledgeApprovals.ReadDecision("""{"id":"x","edited":{"body.uah":3000}}"""));
        Assert.Equal("passed", KnowledgeApprovals.ReadDecision("""{"decision":"d","id":"x","action":"passed","edited":{},"at":"t","sig":"s"}""")!.Action);
    }

    // Vectors from the approvals server's sign.ts (node): Cyrillic, quotes, a newline, an emoji, a tab and a control character.
    private static readonly ApprovalDecision Signed = new(
        "01JBTESTDECISION0000000001",
        "knowledge:result:7f3c1a52-0000-4000-8000-000000000001",
        "passed",
        new() { ["body.uah"] = "3000", ["body.note"] = "Оплата \"перша\"\nдякую 🎉\t\u0001", ["body.metric_value"] = "14" },
        "2026-11-01T10:00:00Z",
        "eda192b81b338438bc167c149a287419115f6249e3da801e80d1213597e3295e");

    [Fact]
    public void I24_signature_matches_the_shell()
    {
        Assert.Equal(
            """{"body.metric_value":"14","body.note":"Оплата \"перша\"\nдякую 🎉\t\u0001","body.uah":"3000"}""",
            ApprovalSignature.CanonicalEdited(Signed.Edited));
        Assert.True(ApprovalSignature.Verify("test-key", Signed));
        Assert.True(ApprovalSignature.Verify("test-key", Signed with { Action = "failed", Edited = [], Sig = "08661CBAC1B52DAD53CDDB94E2B8F8EF5A875D376A978977543FD0E0EFB0A48D" }));
        Assert.False(ApprovalSignature.Verify("other-key", Signed));
        Assert.False(ApprovalSignature.Verify("test-key", Signed with { Action = "failed" }));
        Assert.False(ApprovalSignature.Verify("test-key", Signed with { Sig = "zz" }));
        Assert.False(ApprovalSignature.Verify("test-key", Signed with { At = null }));
    }

    [Fact]
    public async Task I22_start_test_is_written_once_and_a_retry_is_ok()
    {
        await using var db = await postgres.NewAsync();
        var idea = await db.Ideas.CaptureAsync("Tool that grades ideas", new NewEpisode("note", "n1", "h1"));
        var item = KnowledgeApprovals.IdeaItem(Proposal with { Idea = idea }, Delivery);
        var decision = Decide(item, "start_test");
        db.At(20);

        var first = await KnowledgeApprovals.ApplyAsync(db.Ideas, item, decision);
        var rows = (await db.Facts.HistoryAsync(idea, IdeaPredicates.Status)).Count;
        var retry = await KnowledgeApprovals.ApplyAsync(db.Ideas, item, decision);

        Assert.Equal(new ApprovalReply(true, "Testing started"), first);
        Assert.Equal(first, retry);
        Assert.Equal(rows, (await db.Facts.HistoryAsync(idea, IdeaPredicates.Status)).Count);
        Assert.Equal("testing", (await db.Facts.AsOfAsync(idea, IdeaPredicates.Status, Harness.Day(20)))[0].Value);
        Assert.Equal(
            new ApprovalReply(false, "transition_not_allowed status"),
            await KnowledgeApprovals.ApplyAsync(db.Ideas, item, decision with { Decision = "01JBTESTDECISION0000000002" }));
    }

    [Fact]
    public async Task I23_passed_with_uah_writes_an_earned_event_with_amount_date_and_source()
    {
        await using var db = await postgres.NewAsync();
        var idea = await db.Ideas.CaptureAsync("Tool that grades ideas", new NewEpisode("note", "n1", "h1"));
        Assert.True((await db.Ideas.MoveAsync(idea, new IdeaDraft(IdeaStatus.Testing, Proposal.ValueKind, Proposal.Hypothesis, Test), new NewEpisode("note", "n2", "h2"))).Accepted);
        db.At(5);
        var experiment = await db.Ideas.StartExperimentAsync(idea, new NewEpisode("note", "n3", "h3"));
        var item = KnowledgeApprovals.ResultItem(new ResultProposal(experiment, Test), Delivery);
        var decision = Decide(item, "passed", new() { ["body.metric_value"] = "14", ["body.uah"] = "3000" });
        db.At(20);

        var reply = await KnowledgeApprovals.ApplyAsync(db.Ideas, item, decision);
        var retry = await KnowledgeApprovals.ApplyAsync(db.Ideas, item, decision);

        Assert.Equal(new ApprovalReply(true, "Recorded passed, 3000 UAH earned"), reply);
        Assert.Equal(reply, retry);
        var chain = await db.Ideas.ChainAsync(idea);
        Assert.Equal(3000m, chain.EarnedUah);
        Assert.Equal(new ChainStage("earned", Harness.Day(20), 0), chain.Stages[^1]);
        var result = (await db.Facts.AsOfAsync(experiment, IdeaPredicates.Produced, Harness.Day(20))).Single().ObjectEntity!.Value;
        var earned = (await db.Facts.HistoryAsync(result, IdeaPredicates.Earned)).Single();
        var source = (await db.Facts.ExplainAsync(earned.Fact.Id))!.Sources.Single();
        Assert.Equal(("approvals", $"{item.Id}/{decision.Decision}/earned", EpisodeTrust.Owner), (source.SourceKind, source.SourceRef, source.Trust));
    }
}
