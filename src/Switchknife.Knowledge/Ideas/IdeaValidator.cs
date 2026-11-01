namespace Switchknife.Knowledge.Ideas;

/// <summary>Why a draft or a transition was refused. A code and a field, never text from the draft, so a report is safe to log.</summary>
public enum IdeaRejectReason
{
    /// <summary>A required field is null or blank.</summary>
    Missing,

    /// <summary>A number that must be positive is not.</summary>
    NotPositive,

    /// <summary>A number that must not be negative is.</summary>
    Negative,

    /// <summary>The status change is not in the allowed table.</summary>
    TransitionNotAllowed,
}

/// <summary>A refusal.</summary>
/// <param name="Reason">Why.</param>
/// <param name="Field">The field name as stored: <c>value_kind</c>, <c>value_hypothesis.who</c>, <c>cheapest_test.pass_line</c>, <c>status</c>.</param>
public readonly record struct IdeaRejection(IdeaRejectReason Reason, string Field);

/// <summary>
/// The promotion gate. Capture has none: a <see cref="IdeaStatus.Seed"/> is always accepted. The gate sits where hours or money start
/// to be spent, because that is where a missing pass line makes a test impossible to end.
/// </summary>
public static class IdeaValidator
{
    /// <summary>Checks what the draft must carry for its status.</summary>
    /// <returns>Every gap; empty when the draft may be written.</returns>
    public static IReadOnlyList<IdeaRejection> Validate(IdeaDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var found = new List<IdeaRejection>();

        if (draft.Status == IdeaStatus.Killed)
        {
            Require(found, draft.KillReason, "kill_reason");
            return found;
        }

        if (draft.Status == IdeaStatus.Seed)
        {
            return found;
        }

        if (draft.ValueKind is null)
        {
            found.Add(new(IdeaRejectReason.Missing, "value_kind"));
        }

        var h = draft.Hypothesis;
        if (h is null)
        {
            found.Add(new(IdeaRejectReason.Missing, "value_hypothesis"));
        }
        else
        {
            Require(found, h.Who, "value_hypothesis.who");
            Require(found, h.Gain, "value_hypothesis.gain");
            Require(found, h.Evidence, "value_hypothesis.evidence");
            if (draft.ValueKind == ValueKind.Money && h.ExpectedUahPerMonth is not > 0)
            {
                found.Add(new(IdeaRejectReason.NotPositive, "value_hypothesis.expected_uah_per_month"));
            }
        }

        var t = draft.Test;
        if (t is null)
        {
            found.Add(new(IdeaRejectReason.Missing, "cheapest_test"));
        }
        else
        {
            Require(found, t.Action, "cheapest_test.action");
            Require(found, t.Metric, "cheapest_test.metric");
            Require(found, t.PassLine, "cheapest_test.pass_line");
            if (t.CapHours <= 0)
            {
                found.Add(new(IdeaRejectReason.NotPositive, "cheapest_test.cap_hours"));
            }

            if (t.CapUah < 0)
            {
                found.Add(new(IdeaRejectReason.Negative, "cheapest_test.cap_uah"));
            }

            if (t.TimeBoxDays <= 0)
            {
                found.Add(new(IdeaRejectReason.NotPositive, "cheapest_test.time_box_days"));
            }
        }

        return found;
    }

    /// <summary>Whether a status may follow another. A seed cannot skip the test; a killed idea may return, through the full gate.</summary>
    public static bool CanMove(IdeaStatus from, IdeaStatus to) => (from, to) switch
    {
        (IdeaStatus.Seed, IdeaStatus.Testing or IdeaStatus.Killed) => true,
        (IdeaStatus.Testing, IdeaStatus.Validated or IdeaStatus.Killed) => true,
        (IdeaStatus.Validated, IdeaStatus.Earning or IdeaStatus.Killed) => true,
        (IdeaStatus.Earning, IdeaStatus.Killed) => true,
        (IdeaStatus.Killed, IdeaStatus.Seed or IdeaStatus.Testing) => true,
        _ => false,
    };

    /// <summary>Checks the move, then the draft.</summary>
    public static IReadOnlyList<IdeaRejection> Validate(IdeaStatus from, IdeaDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!CanMove(from, draft.Status))
        {
            return [new(IdeaRejectReason.TransitionNotAllowed, "status")];
        }

        return Validate(draft);
    }

    private static void Require(List<IdeaRejection> found, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            found.Add(new(IdeaRejectReason.Missing, field));
        }
    }
}
