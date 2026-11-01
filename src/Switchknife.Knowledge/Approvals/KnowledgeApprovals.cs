using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Switchknife.Facts;
using Switchknife.Knowledge.Ideas;
using Switchknife.Knowledge.Tools;

namespace Switchknife.Knowledge.Approvals;

/// <summary>A refusal of a decision: a snake_case code and the field (<c>id</c>, <c>action</c>, <c>body.uah</c>), never text from the decision.</summary>
public sealed record DecisionRejection(string Reason, string Field);

/// <summary>What the idea item shows: the seed and, when the host drafted them, the value kind, hypothesis and test to start.</summary>
public sealed record IdeaProposal(Guid Idea, string Seed, ValueKind? ValueKind = null, ValueHypothesis? Hypothesis = null, CheapestTest? Test = null);

/// <summary>What the result item shows: the test whose outcome is asked for.</summary>
public sealed record ResultProposal(Guid Experiment, CheapestTest Test);

/// <summary>What the host adds to every item: the callback (its own webhook), and optionally an expiry, evidence and links.</summary>
public sealed record ItemDelivery(string Callback, DateTimeOffset? ExpiresAt = null, IReadOnlyList<ApprovalEvidence>? Evidence = null, IReadOnlyList<ApprovalLink>? Links = null);

/// <summary>The buttons of the idea item.</summary>
public enum IdeaChoice
{
    /// <summary>Promote to <c>testing</c> with the (edited) hypothesis and test.</summary>
    StartTest,

    /// <summary>Change nothing.</summary>
    KeepSeed,

    /// <summary>Kill, with the (edited) reason.</summary>
    Kill,
}

/// <summary>A parsed idea decision. <see cref="Draft"/> passed <see cref="IdeaValidator.Validate(IdeaDraft)"/>; the transition is checked when it is written.</summary>
public sealed record IdeaDecision(Guid? Idea, IdeaChoice? Choice, IdeaDraft? Draft, IReadOnlyList<DecisionRejection> Rejections)
{
    /// <summary>True when there is nothing to refuse.</summary>
    public bool Accepted => Rejections.Count == 0;
}

/// <summary>A parsed result decision. <see cref="Uah"/> above zero becomes an <c>earned</c> event dated <see cref="At"/>.</summary>
public sealed record ResultDecision(
    Guid? Experiment, ResultOutcome? Outcome, decimal? MetricValue, decimal? Uah, string? Note, DateTimeOffset? At, IReadOnlyList<DecisionRejection> Rejections)
{
    /// <summary>True when there is nothing to refuse.</summary>
    public bool Accepted => Rejections.Count == 0;
}

/// <summary>
/// The approvals items of the ideas chain: <c>knowledge:idea:&lt;idea&gt;</c> (start a test, keep as seed, kill) and
/// <c>knowledge:result:&lt;experiment&gt;</c> (passed, failed, inconclusive, with metric, UAH and note). The kit builds items and parses decisions;
/// the host posts items, verifies the signature (<see cref="ApprovalSignature"/>), refuses stale or seen decisions, and answers the shell.
/// </summary>
public static class KnowledgeApprovals
{
    /// <summary>The <c>source</c> of every item.</summary>
    public const string Source = "knowledge";

    private const string IdeaPrefix = "knowledge:idea:";
    private const string ResultPrefix = "knowledge:result:";
    private const string Episodes = "approvals";

    /// <summary>The idea item: the seed read-only; value kind, hypothesis, test and kill reason editable.</summary>
    public static ApprovalItem IdeaItem(IdeaProposal proposal, ItemDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.Seed);
        var h = proposal.Hypothesis;
        var t = proposal.Test;
        var body = new Dictionary<string, string>
        {
            ["seed"] = proposal.Seed,
            ["value_kind"] = proposal.ValueKind switch { ValueKind.Money => "money", ValueKind.People => "people", _ => "" },
            ["who"] = h?.Who ?? "",
            ["gain"] = h?.Gain ?? "",
            ["evidence"] = h?.Evidence ?? "",
            ["expected_uah_per_month"] = Number(h?.ExpectedUahPerMonth),
            ["test_action"] = t?.Action ?? "",
            ["cap_hours"] = Number(t?.CapHours),
            ["cap_uah"] = Number(t?.CapUah),
            ["metric"] = t?.Metric ?? "",
            ["pass_line"] = t?.PassLine ?? "",
            ["time_box_days"] = t?.TimeBoxDays?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["kill_reason"] = "",
        };
        return Item(
            $"{IdeaPrefix}{proposal.Idea}",
            $"Idea: {Clip(proposal.Seed)}",
            h is null ? "Draft a hypothesis and a test to start, keep as a seed, or kill" : $"Start a test: {Clip(t?.PassLine ?? h.Gain)}",
            body,
            [new("start_test", "Start test", "primary"), new("keep_seed", "Keep as seed"), new("kill", "Kill", "danger", Confirm: true)],
            [.. body.Keys.Where(k => k != "seed")],
            delivery);
    }

    /// <summary>The result item: the test and its pass line read-only; metric value, UAH earned and note editable.</summary>
    public static ApprovalItem ResultItem(ResultProposal proposal, ItemDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(proposal.Test);
        var body = new Dictionary<string, string>
        {
            ["test_action"] = proposal.Test.Action,
            ["metric"] = proposal.Test.Metric,
            ["pass_line"] = proposal.Test.PassLine,
            ["metric_value"] = "",
            ["uah"] = "",
            ["note"] = "",
        };
        return Item(
            $"{ResultPrefix}{proposal.Experiment}",
            $"Result: {Clip(proposal.Test.Action)}",
            $"Pass line: {Clip(proposal.Test.PassLine)}",
            body,
            [new("passed", "Passed", "primary"), new("failed", "Failed"), new("inconclusive", "Inconclusive")],
            ["metric_value", "uah", "note"],
            delivery);
    }

    /// <summary>Reads a decision POST body; null when it is not the contract's JSON.</summary>
    public static ApprovalDecision? ReadDecision(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, ApprovalJsonContext.Default.ApprovalDecision);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a decision on the idea item the host posted. Writes nothing.</summary>
    public static IdeaDecision ParseIdea(ApprovalItem item, ApprovalDecision decision)
    {
        var (rejections, body, _) = Check(item, decision);
        var idea = Target(item.Id, IdeaPrefix);
        if (idea is null)
        {
            rejections.Add(new("invalid", "id"));
        }

        var choice = decision.Action switch
        {
            "start_test" => IdeaChoice.StartTest,
            "keep_seed" => IdeaChoice.KeepSeed,
            "kill" => IdeaChoice.Kill,
            _ => (IdeaChoice?)null,
        };
        if (rejections.Count > 0 || choice is IdeaChoice.KeepSeed)
        {
            return new IdeaDecision(idea, choice, null, rejections);
        }

        IdeaDraft draft;
        if (choice is IdeaChoice.Kill)
        {
            draft = new IdeaDraft(IdeaStatus.Killed, KillReason: Text(body, "kill_reason"));
        }
        else
        {
            var kind = body["value_kind"].Trim() switch
            {
                "money" => ValueKind.Money,
                "people" => ValueKind.People,
                "" => (ValueKind?)null,
                _ => Refuse<ValueKind>(rejections, "invalid", "value_kind"),
            };
            var expected = ReadDecimal(body, "expected_uah_per_month", rejections, required: false);
            var capHours = ReadDecimal(body, "cap_hours", rejections, required: true);
            var capUah = ReadDecimal(body, "cap_uah", rejections, required: true);
            var timeBox = ReadInteger(body, "time_box_days", rejections);
            if (rejections.Count > 0)
            {
                return new IdeaDecision(idea, choice, null, rejections);
            }

            draft = new IdeaDraft(
                IdeaStatus.Testing,
                kind,
                new ValueHypothesis(body["who"].Trim(), body["gain"].Trim(), body["evidence"].Trim(), expected),
                new CheapestTest(body["test_action"].Trim(), capHours!.Value, capUah!.Value, body["metric"].Trim(), body["pass_line"].Trim(), timeBox));
        }

        rejections.AddRange(IdeaValidator.Validate(draft).Select(r => new DecisionRejection(KnowledgeTools.Code(r.Reason), BodyPath(r.Field))));
        return new IdeaDecision(idea, choice, rejections.Count == 0 ? draft : null, rejections);
    }

    /// <summary>Parses a decision on the result item the host posted. Writes nothing.</summary>
    public static ResultDecision ParseResult(ApprovalItem item, ApprovalDecision decision)
    {
        var (rejections, body, at) = Check(item, decision);
        var experiment = Target(item.Id, ResultPrefix);
        if (experiment is null)
        {
            rejections.Add(new("invalid", "id"));
        }

        var outcome = decision.Action switch
        {
            "passed" => ResultOutcome.Passed,
            "failed" => ResultOutcome.Failed,
            "inconclusive" => ResultOutcome.Inconclusive,
            _ => (ResultOutcome?)null,
        };
        var metric = ReadDecimal(body, "metric_value", rejections, required: false);
        var uah = ReadDecimal(body, "uah", rejections, required: false);
        if (uah < 0)
        {
            rejections.Add(new("negative", "body.uah"));
        }

        return new ResultDecision(experiment, outcome, metric, uah, Text(body, "note"), at, rejections);
    }

    /// <summary>
    /// Parses and writes a decision on either item, and returns the answer for the shell. Each decision is its own source
    /// (<c>approvals</c>, <c>&lt;item&gt;/&lt;decision&gt;</c>), so a retry of the same decision is a duplicate and never retires what another decision wrote.
    /// </summary>
    public static async Task<ApprovalReply> ApplyAsync(IdeaStore ideas, ApprovalItem item, ApprovalDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ideas);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(decision);
        if (item.Id.StartsWith(ResultPrefix, StringComparison.Ordinal))
        {
            var result = ParseResult(item, decision);
            if (!result.Accepted)
            {
                return Refused(result.Rejections);
            }

            var recorded = await ideas.RecordResultAsync(
                result.Experiment!.Value, result.Outcome!.Value, Episode(item, decision, result.At!.Value, ""), result.MetricValue, result.Note, item.Id, cancellationToken)
                .ConfigureAwait(false);
            if (result.Uah is { } uah && uah > 0)
            {
                await ideas.RecordEarnedAsync(recorded, uah, result.At!.Value, Episode(item, decision, result.At!.Value, "/earned"), cancellationToken).ConfigureAwait(false);
                return new ApprovalReply(true, $"Recorded {IdeaStore.OutcomeText(result.Outcome!.Value)}, {uah.ToString(CultureInfo.InvariantCulture)} UAH earned");
            }

            return new ApprovalReply(true, $"Recorded {IdeaStore.OutcomeText(result.Outcome!.Value)}");
        }

        var parsed = ParseIdea(item, decision);
        if (!parsed.Accepted)
        {
            return Refused(parsed.Rejections);
        }

        if (parsed.Choice is IdeaChoice.KeepSeed)
        {
            return new ApprovalReply(true, "Kept as a seed");
        }

        var done = new ApprovalReply(true, parsed.Choice is IdeaChoice.Kill ? "Killed" : "Testing started");
        var episode = Episode(item, decision, Instant(decision.At)!.Value, "");
        if (await ideas.SeenAsync(episode, cancellationToken).ConfigureAwait(false))
        {
            return done;
        }

        var moved = await ideas.MoveAsync(parsed.Idea!.Value, parsed.Draft!, episode, cancellationToken).ConfigureAwait(false);
        return moved.Accepted
            ? done
            : Refused([.. moved.Rejections.Select(r => new DecisionRejection(KnowledgeTools.Code(r.Reason), BodyPath(r.Field)))]);
    }

    private static ApprovalItem Item(
        string id, string title, string summary, Dictionary<string, string> body, ApprovalAction[] actions, string[] editable, ItemDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(delivery.Callback);
        return new ApprovalItem(
            Source, id, "decision", 3, title, summary, delivery.Evidence ?? [], body, [.. editable.Select(k => $"body.{k}")], delivery.Links ?? [], actions, delivery.ExpiresAt, delivery.Callback);
    }

    // The checks every decision gets: the item, the action, the time, and only editable paths. Body is the item's merged with the edits.
    private static (List<DecisionRejection> Rejections, Dictionary<string, string> Body, DateTimeOffset? At) Check(ApprovalItem item, ApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(decision);
        var rejections = new List<DecisionRejection>();
        if (string.IsNullOrWhiteSpace(decision.Id))
        {
            rejections.Add(new("missing", "id"));
        }
        else if (decision.Id != item.Id)
        {
            rejections.Add(new("invalid", "id"));
        }

        if (string.IsNullOrWhiteSpace(decision.Decision))
        {
            rejections.Add(new("missing", "decision"));
        }

        if (string.IsNullOrWhiteSpace(decision.Action))
        {
            rejections.Add(new("missing", "action"));
        }
        else if (!item.Actions.Any(a => a.Id == decision.Action))
        {
            rejections.Add(new("invalid", "action"));
        }

        var at = Instant(decision.At);
        if (at is null)
        {
            rejections.Add(new(string.IsNullOrWhiteSpace(decision.At) ? "missing" : "invalid", "at"));
        }

        var body = new Dictionary<string, string>(item.Body);
        foreach (var (path, value) in decision.Edited ?? [])
        {
            if (!item.Editable.Contains(path))
            {
                rejections.Add(new("not_editable", path));
            }
            else
            {
                body[path["body.".Length..]] = value ?? "";
            }
        }

        return (rejections, body, at);
    }

    private static NewEpisode Episode(ApprovalItem item, ApprovalDecision decision, DateTimeOffset at, string suffix)
    {
        var signed = $"{decision.Decision}|{decision.Action}|{ApprovalSignature.CanonicalEdited(decision.Edited)}";
        return new NewEpisode(Episodes, $"{item.Id}/{decision.Decision}{suffix}", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signed))), at, EpisodeTrust.Owner);
    }

    private static ApprovalReply Refused(IReadOnlyList<DecisionRejection> rejections) =>
        new(false, string.Join(", ", rejections.Select(r => $"{r.Reason} {r.Field}")));

    private static Guid? Target(string id, string prefix) =>
        id.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(id.AsSpan(prefix.Length), "D", out var guid) ? guid : null;

    private static DateTimeOffset? Instant(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static decimal? ReadDecimal(Dictionary<string, string> body, string key, List<DecisionRejection> rejections, bool required)
    {
        var text = body[key].Trim();
        if (text.Length == 0)
        {
            return required ? Refuse<decimal>(rejections, "missing", key) : null;
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : Refuse<decimal>(rejections, "invalid", key);
    }

    private static int? ReadInteger(Dictionary<string, string> body, string key, List<DecisionRejection> rejections)
    {
        var text = body[key].Trim();
        return text.Length == 0 ? null
            : int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value
            : Refuse<int>(rejections, "invalid", key);
    }

    private static string? Text(Dictionary<string, string> body, string key) => body[key].Trim() is { Length: > 0 } text ? text : null;

    private static T? Refuse<T>(List<DecisionRejection> rejections, string reason, string key) where T : struct
    {
        rejections.Add(new(reason, $"body.{key}"));
        return null;
    }

    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    // Validator fields (value_hypothesis.who, cheapest_test.action) as the item's body paths; status and idea have none.
    private static string BodyPath(string field) => field switch
    {
        "status" or "idea" => field,
        "cheapest_test.action" => "body.test_action",
        _ when field.StartsWith("value_hypothesis.", StringComparison.Ordinal) => $"body.{field["value_hypothesis.".Length..]}",
        _ when field.StartsWith("cheapest_test.", StringComparison.Ordinal) => $"body.{field["cheapest_test.".Length..]}",
        _ => $"body.{field}",
    };

    private static string Clip(string text) =>
        text.Length <= 80 ? text : string.Concat(text.AsSpan(0, char.IsHighSurrogate(text[78]) ? 78 : 79), "…");
}
