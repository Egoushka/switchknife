namespace Switchknife.Knowledge.Ideas;

/// <summary>What a recorded test showed.</summary>
public enum ResultOutcome
{
    /// <summary>The metric reached the pass line.</summary>
    Passed,

    /// <summary>The metric missed the pass line.</summary>
    Failed,

    /// <summary>The test ran but cannot decide.</summary>
    Inconclusive,
}

/// <summary>One link of the chain an idea reached.</summary>
/// <param name="Link">The predicate: <c>inspired</c>, <c>tested_by</c>, <c>produced</c> or <c>earned</c>.</param>
/// <param name="ReachedAt">The earliest live fact of that link.</param>
/// <param name="Days">Days until the next link was reached, or until now for the last one.</param>
public sealed record ChainStage(string Link, DateTimeOffset ReachedAt, double Days);

/// <summary>An idea's way from what inspired it to money earned.</summary>
/// <param name="Idea">The idea.</param>
/// <param name="Status">Its live status in lower case; null when the idea has none.</param>
/// <param name="Stages">The links reached, in chain order; empty for an idea with no links.</param>
/// <param name="EarnedUah">The sum of live <c>earned</c> facts under its results. Only this is income.</param>
public sealed record IdeaChain(Guid Idea, string? Status, IReadOnlyList<ChainStage> Stages, decimal EarnedUah);

/// <summary>Ideas per live status.</summary>
public sealed record StatusCounts(int Seed, int Testing, int Validated, int Killed, int Earning);

/// <summary>Ideas that ever held each status of the main line, and the conversions between them (null when nothing reached the earlier status).</summary>
public sealed record IdeaFunnel(
    int Seed,
    int Testing,
    int Validated,
    int Earning,
    double? SeedToTesting,
    double? TestingToValidated,
    double? ValidatedToEarning);

/// <summary>The median days an idea spent in a status, over stays that ended; null when none ended.</summary>
public sealed record StageDays(double? Seed, double? Testing, double? Validated);

/// <summary>An idea in <c>testing</c> longer than its own time box.</summary>
public sealed record StuckTest(Guid Idea, string? Title, DateTimeOffset Since, int TimeBoxDays, double DaysInTesting);

/// <summary>
/// UAH estimated and earned, as separate numbers that are never added: the estimate is the sum of
/// <see cref="ValueHypothesis.ExpectedUahPerMonth"/> over ideas in <c>testing</c>; earned counts only <c>earned</c> facts.
/// </summary>
/// <param name="EstimatedUahPerMonth">Estimates of the ideas now in <c>testing</c>.</param>
/// <param name="EarnedUah">Every live <c>earned</c> fact.</param>
/// <param name="EarnedUahThisMonth">Live <c>earned</c> facts dated in the current UTC month; the number the target is about.</param>
/// <param name="TargetUahPerMonth">The caller's target, echoed; null when none was given.</param>
public sealed record IdeaMoney(decimal EstimatedUahPerMonth, decimal EarnedUah, decimal EarnedUahThisMonth, decimal? TargetUahPerMonth);

/// <summary>A measure the board cannot compute yet, and why; reported instead of a zero.</summary>
public sealed record AbsentMeasure(string Name, string Reason);

/// <summary>The analytics of the <c>ideas</c> namespace: funnel, days per stage, stuck tests and money.</summary>
/// <param name="Statuses">Ideas per live status.</param>
/// <param name="Funnel">Ideas that ever reached each status, and conversions.</param>
/// <param name="MedianDays">Median days per status.</param>
/// <param name="StuckTests">Tests older than their time box.</param>
/// <param name="UnboxedTests">Ideas in <c>testing</c> whose test has no time box.</param>
/// <param name="Money">Estimated and earned UAH, apart.</param>
/// <param name="LateYield">Null until links and read counts exist (<see cref="Absent"/> says why).</param>
/// <param name="QuestionCoverage">Null until the search query log exists.</param>
/// <param name="Absent">The measures reported as null, with reasons.</param>
public sealed record IdeaBoard(
    StatusCounts Statuses,
    IdeaFunnel Funnel,
    StageDays MedianDays,
    IReadOnlyList<StuckTest> StuckTests,
    IReadOnlyList<Guid> UnboxedTests,
    IdeaMoney Money,
    double? LateYield,
    double? QuestionCoverage,
    IReadOnlyList<AbsentMeasure> Absent);
