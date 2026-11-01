using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Switchknife.Facts;
using Switchknife.Knowledge.Ideas;

namespace Switchknife.Knowledge.Tools;

/// <summary>Where the text of a capture or a promotion came from.</summary>
/// <param name="Kind">The source kind, e.g. <c>chat</c> or <c>note</c>.</param>
/// <param name="Ref">The consumer's reference inside that kind; the same kind and ref name the same idea.</param>
public sealed record ToolSource(string? Kind, string? Ref);

/// <summary>The arguments of <c>knowledge_idea</c>.</summary>
/// <param name="Action"><c>capture</c> or <c>promote</c>.</param>
/// <param name="Source">Required for both actions.</param>
/// <param name="Text">The idea's text; required for <c>capture</c>.</param>
/// <param name="Idea">The idea to move; required for <c>promote</c>.</param>
/// <param name="Status">The target status for <c>promote</c>: <c>testing</c>, <c>validated</c>, <c>earning</c>, <c>killed</c> or <c>seed</c>.</param>
/// <param name="ValueKind"><c>people</c> or <c>money</c>.</param>
/// <param name="Hypothesis">Who gains what, by how much, on what evidence.</param>
/// <param name="Test">The cheapest test and its pass line.</param>
/// <param name="KillReason">Why, for <c>killed</c>.</param>
public sealed record IdeaToolInput(
    string? Action,
    ToolSource? Source,
    string? Text = null,
    Guid? Idea = null,
    string? Status = null,
    string? ValueKind = null,
    ValueHypothesis? Hypothesis = null,
    CheapestTest? Test = null,
    string? KillReason = null);

/// <summary>A refusal of <c>knowledge_idea</c>: a snake_case code and the field, never text from the arguments.</summary>
public sealed record ToolRejection(string Reason, string Field);

/// <summary>The result of <c>knowledge_idea</c>.</summary>
/// <param name="Idea">The idea captured or moved; null when refused before it was known.</param>
/// <param name="Written">True when the facts were written (or were already there, see <paramref name="Duplicate"/>).</param>
/// <param name="Duplicate">True when the same source text was seen before, so nothing new was written.</param>
/// <param name="Rejections">Why nothing was written; empty when <paramref name="Written"/>.</param>
public sealed record IdeaToolResult(Guid? Idea, bool Written, bool Duplicate, IReadOnlyList<ToolRejection> Rejections);

/// <summary>The arguments of <c>knowledge_board</c>.</summary>
/// <param name="TargetUahPerMonth">The income target to show next to earned UAH; optional.</param>
public sealed record BoardToolInput(decimal? TargetUahPerMonth = null);

/// <summary>Source-generated serialization of the tool arguments and results.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(IdeaToolInput))]
[JsonSerializable(typeof(IdeaToolResult))]
[JsonSerializable(typeof(BoardToolInput))]
public sealed partial class KnowledgeToolsJsonContext : JsonSerializerContext;

/// <summary>The tool definitions of the ideas chain: <c>knowledge_idea</c> and <c>knowledge_board</c>. Both are private (namespace <c>ideas</c>).</summary>
public static class KnowledgeTools
{
    /// <summary>Name of the capture and promote tool.</summary>
    public const string IdeaName = "knowledge_idea";

    /// <summary>Name of the analytics tool.</summary>
    public const string BoardName = "knowledge_board";

    /// <summary>Both tools over one store.</summary>
    /// <param name="ideas">The store.</param>
    /// <param name="trust">The trust given to what a tool call writes; the caller is an agent unless the host knows better.</param>
    public static IReadOnlyList<KnowledgeTool> All(IdeaStore ideas, EpisodeTrust trust = EpisodeTrust.Agent) => [Idea(ideas, trust), Board(ideas)];

    /// <summary>
    /// <c>knowledge_idea</c>: <c>capture</c> writes a seed and checks only that text and a source are present; <c>promote</c>
    /// runs <see cref="IdeaValidator"/> and returns its codes on refusal.
    /// </summary>
    public static KnowledgeTool Idea(IdeaStore ideas, EpisodeTrust trust = EpisodeTrust.Agent)
    {
        ArgumentNullException.ThrowIfNull(ideas);
        return new KnowledgeTool(
            IdeaName,
            "Capture an idea as a seed (action=capture: text and source, nothing else is checked), or promote it "
                + "(action=promote: idea, status and, from testing on, value_kind, hypothesis and test with a pass line). "
                + "A refused promotion writes nothing and returns codes.",
            Schema(KnowledgeToolsJsonContext.Default.IdeaToolInput),
            Private: true,
            (json, ct) => IdeaAsync(ideas, trust, json, ct));
    }

    /// <summary><c>knowledge_board</c>: the funnel, days per stage, stuck tests, and estimated and earned UAH as separate numbers.</summary>
    public static KnowledgeTool Board(IdeaStore ideas)
    {
        ArgumentNullException.ThrowIfNull(ideas);
        return new KnowledgeTool(
            BoardName,
            "The ideas board: counts per status, funnel and conversions, median days per stage, tests past their time box, "
                + "and money with estimated UAH (hypotheses of ideas in testing) and earned UAH apart. Measures without data are null with a reason.",
            Schema(KnowledgeToolsJsonContext.Default.BoardToolInput),
            Private: true,
            async (json, ct) =>
            {
                var input = Parse(json, KnowledgeToolsJsonContext.Default.BoardToolInput) ?? new BoardToolInput();
                var board = await ideas.BoardAsync(input.TargetUahPerMonth, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(board, IdeaBoardJsonContext.Default.IdeaBoard);
            });
    }

    private static async Task<string> IdeaAsync(IdeaStore ideas, EpisodeTrust trust, string json, CancellationToken cancellationToken)
    {
        var result = Parse(json, KnowledgeToolsJsonContext.Default.IdeaToolInput) is { } input
            ? await RunAsync(ideas, trust, input, cancellationToken).ConfigureAwait(false)
            : Refused(null, new ToolRejection("invalid", "arguments"));
        return JsonSerializer.Serialize(result, KnowledgeToolsJsonContext.Default.IdeaToolResult);
    }

    private static async Task<IdeaToolResult> RunAsync(IdeaStore ideas, EpisodeTrust trust, IdeaToolInput input, CancellationToken cancellationToken)
    {
        var missing = new List<ToolRejection>();
        if (string.IsNullOrWhiteSpace(input.Source?.Kind) || string.IsNullOrWhiteSpace(input.Source.Ref))
        {
            missing.Add(new ToolRejection("missing", "source"));
        }

        switch (input.Action)
        {
            case "capture":
                if (string.IsNullOrWhiteSpace(input.Text))
                {
                    missing.Add(new ToolRejection("missing", "text"));
                }

                if (missing.Count > 0)
                {
                    return Refused(null, [.. missing]);
                }

                var source = input.Source!;
                var episode = new NewEpisode(source.Kind!, source.Ref!, Hash(input.Text!), Trust: trust);
                var capture = await ideas.CaptureAsync(new IssueSeed(episode, $"{source.Kind}:{source.Ref}", input.Text!.Trim()), cancellationToken).ConfigureAwait(false);
                return new IdeaToolResult(capture.Idea, true, capture.Ingest.Duplicate, []);

            case "promote":
                if (input.Idea is null)
                {
                    missing.Add(new ToolRejection("missing", "idea"));
                }

                var status = input.Status is { } s ? IdeaStore.ParseStatus(s) : null;
                if (status is null)
                {
                    missing.Add(new ToolRejection(input.Status is null ? "missing" : "invalid", "status"));
                }

                var kind = input.ValueKind switch
                {
                    null => (ValueKind?)null,
                    "money" => Ideas.ValueKind.Money,
                    "people" => Ideas.ValueKind.People,
                    _ => null,
                };
                if (input.ValueKind is not null && kind is null)
                {
                    missing.Add(new ToolRejection("invalid", "value_kind"));
                }

                if (missing.Count > 0)
                {
                    return Refused(input.Idea, [.. missing]);
                }

                var draft = new IdeaDraft(status!.Value, kind, input.Hypothesis, input.Test, input.KillReason);
                var promoteEpisode = new NewEpisode(
                    input.Source!.Kind!, input.Source.Ref!, Hash(JsonSerializer.Serialize(input, KnowledgeToolsJsonContext.Default.IdeaToolInput)), Trust: trust);
                var moved = await ideas.MoveAsync(input.Idea!.Value, draft, promoteEpisode, cancellationToken).ConfigureAwait(false);
                return moved.Accepted
                    ? new IdeaToolResult(input.Idea, true, moved.Ingest!.Duplicate, [])
                    : Refused(input.Idea, [.. moved.Rejections.Select(r => new ToolRejection(Code(r.Reason), r.Field))]);

            default:
                return Refused(null, [.. missing, new ToolRejection(input.Action is null ? "missing" : "invalid", "action")]);
        }
    }

    private static IdeaToolResult Refused(Guid? idea, params ToolRejection[] rejections) => new(idea, false, false, rejections);

    internal static string Code(IdeaRejectReason reason) => reason switch
    {
        IdeaRejectReason.Missing => "missing",
        IdeaRejectReason.NotPositive => "not_positive",
        IdeaRejectReason.Negative => "negative",
        IdeaRejectReason.TransitionNotAllowed => "transition_not_allowed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static T? Parse<T>(string json, JsonTypeInfo<T> info) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject Schema<T>(JsonTypeInfo<T> info) =>
        JsonSchemaExporter.GetJsonSchemaAsNode(info, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }).AsObject();
}
