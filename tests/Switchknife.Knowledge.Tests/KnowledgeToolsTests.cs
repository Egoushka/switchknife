using System.Text.Json;
using Npgsql;
using Switchknife.Knowledge.Ideas;
using Switchknife.Knowledge.Tools;

namespace Switchknife.Knowledge.Tests;

/// <summary>Spec <c>docs/specs/2026-11-01-knowledge-k3-ideas.md</c>, checks I20-I21.</summary>
public sealed class KnowledgeToolsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Source = """{"kind":"chat","ref":"c1"}""";

    private static IReadOnlyList<KnowledgeTool> Offline()
    {
        // No connection is opened until a handler runs.
        return KnowledgeTools.All(new IdeaStore(NpgsqlDataSource.Create("Host=unused")));
    }

    private static async Task<IdeaToolResult> CallAsync(Harness db, string json)
    {
        var tool = KnowledgeTools.Idea(db.Ideas);
        return JsonSerializer.Deserialize(await tool.Handler(json, CancellationToken.None), KnowledgeToolsJsonContext.Default.IdeaToolResult)!;
    }

    [Fact]
    public void I20_tools_have_name_description_schema_and_handler()
    {
        var tools = Offline();

        Assert.Equal([KnowledgeTools.IdeaName, KnowledgeTools.BoardName], tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.All(tools, t => Assert.NotNull(t.Handler));
        var idea = tools[0].InputSchema["properties"]!.AsObject();
        Assert.Equal(
            ["action", "source", "text", "idea", "status", "value_kind", "hypothesis", "test", "kill_reason"],
            idea.Select(p => p.Key));
        Assert.NotNull(idea["test"]!["properties"]!["pass_line"]);
        Assert.NotNull(tools[1].InputSchema["properties"]!["target_uah_per_month"]);
    }

    [Fact]
    public void I21_both_tools_are_private()
    {
        Assert.All(Offline(), t => Assert.True(t.Private, t.Name));
    }

    [Fact]
    public async Task I20_capture_checks_only_text_and_source()
    {
        await using var db = await postgres.NewAsync();

        var first = await CallAsync(db, $$"""{"action":"capture","text":"Tool that grades ideas","source":{{Source}}}""");
        var again = await CallAsync(db, $$"""{"action":"capture","text":"Tool that grades ideas","source":{{Source}}}""");
        var empty = await CallAsync(db, """{"action":"capture","text":" "}""");

        Assert.True(first.Written);
        Assert.Empty(first.Rejections);
        Assert.Equal((true, first.Idea), (again.Duplicate, again.Idea));
        Assert.Equal([new ToolRejection("missing", "source"), new ToolRejection("missing", "text")], empty.Rejections);
        Assert.False(empty.Written);
    }

    [Fact]
    public async Task I20_promote_returns_validator_codes_and_writes_nothing_on_refusal()
    {
        await using var db = await postgres.NewAsync();
        var idea = (await CallAsync(db, $$"""{"action":"capture","text":"x","source":{{Source}}}""")).Idea;
        const string Hypothesis = """{"who":"freelancers","gain":"save 2 h a week","evidence":"3 interviews","expected_uah_per_month":5000}""";

        var refused = await CallAsync(db, $$"""{"action":"promote","idea":"{{idea}}","status":"testing","value_kind":"money","hypothesis":{{Hypothesis}},"source":{{Source}}}""");
        var accepted = await CallAsync(db, $$"""
            {"action":"promote","idea":"{{idea}}","status":"testing","value_kind":"money","hypothesis":{{Hypothesis}},
             "test":{"action":"landing page","cap_hours":6,"cap_uah":0,"metric":"signups","pass_line":"10 in 14 days","time_box_days":14},"source":{{Source}}}
            """);

        Assert.Equal([new ToolRejection("missing", "cheapest_test")], refused.Rejections);
        Assert.False(refused.Written);
        Assert.True(accepted.Written);
        Assert.Equal(1, (await db.Ideas.BoardAsync()).Statuses.Testing);
    }

    [Theory]
    [InlineData("not json", "invalid", "arguments")]
    [InlineData("""{"action":"delete","source":{"kind":"chat","ref":"c1"}}""", "invalid", "action")]
    [InlineData("""{"source":{"kind":"chat","ref":"c1"}}""", "missing", "action")]
    [InlineData("""{"action":"promote","idea":"00000000-0000-0000-0000-000000000001","status":"done","source":{"kind":"chat","ref":"c1"}}""", "invalid", "status")]
    public async Task I20_bad_arguments_come_back_as_codes(string json, string reason, string field)
    {
        var tool = Offline()[0];

        var result = JsonSerializer.Deserialize(await tool.Handler(json, CancellationToken.None), KnowledgeToolsJsonContext.Default.IdeaToolResult)!;

        Assert.Equal([new ToolRejection(reason, field)], result.Rejections);
    }

    [Fact]
    public async Task Board_tool_returns_the_board_payload()
    {
        await using var db = await postgres.NewAsync();

        var json = await KnowledgeTools.Board(db.Ideas).Handler("""{"target_uah_per_month":60000}""", CancellationToken.None);

        Assert.Contains("\"target_uah_per_month\":60000", json, StringComparison.Ordinal);
        Assert.Contains("\"late_yield\":null", json, StringComparison.Ordinal);
    }
}
