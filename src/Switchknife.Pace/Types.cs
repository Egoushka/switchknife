namespace Switchknife.Pace;

/// <summary>What a caller is told to do with a job.</summary>
public enum Decision
{
    /// <summary>Run it on the subscription.</summary>
    Allow = 0,

    /// <summary>Do not start it; try again after <see cref="Admission.RetryAfter"/>.</summary>
    Hold = 1,

    /// <summary>Run it on the owner's own API credential.</summary>
    Api = 2,

    /// <summary>Run it on a cheaper model, on the owner's own API credential.</summary>
    Cheap = 3,
}

/// <summary>The ledger's verdict on a consumer or on the account. The first matching state in this order wins.</summary>
public enum PaceState
{
    /// <summary>Inside the pace line, or no meter reading yet.</summary>
    Ok = 0,

    /// <summary>Used plus reserved is past the pace line, or the account meter is past the total pace line.</summary>
    OverPace = 1,

    /// <summary>The plan refused a request; limited until the reset the reporter named.</summary>
    Limited = 2,

    /// <summary>The newest meter reading is older than <see cref="Policy.StaleAfter"/>.</summary>
    Stale = 3,
}

/// <summary>Whether <see cref="Ledger.Admit"/> may answer anything but <see cref="Decision.Allow"/>.</summary>
public enum PaceMode
{
    /// <summary>Always <see cref="Decision.Allow"/>; <see cref="Admission.Would"/> says what <see cref="Enforce"/> would answer.</summary>
    Observe = 0,

    /// <summary>The answer is <see cref="Admission.Would"/>.</summary>
    Enforce = 1,
}

/// <summary>A meter the plan reports.</summary>
public enum MeterWindow
{
    /// <summary>The rolling 5-hour window.</summary>
    FiveHour = 0,

    /// <summary>The weekly window. The ledger's week is keyed by its reset time.</summary>
    SevenDay = 1,

    /// <summary>A weekly window scoped to a model family (<see cref="MeterReading.Scope"/>). Recorded, not paced.</summary>
    SevenDayScoped = 2,
}

/// <summary>Which credential a job ran on.</summary>
public enum UsageRoute
{
    /// <summary>The subscription: counts toward the consumer's share.</summary>
    Plan = 0,

    /// <summary>The owner's API credential: counts toward a dollar cap, not the share.</summary>
    Api = 1,
}

/// <summary>Which attribution estimate a consumer's <see cref="ConsumerStatus.Used"/> comes from.</summary>
public enum Estimator
{
    /// <summary>Each meter rise split by the consumers' reported use since the last rise.</summary>
    Apportion = 0,
}

/// <summary>One reading of the plan's meter.</summary>
/// <param name="Window">Which meter.</param>
/// <param name="Utilization">0 to 1, the share of the window used.</param>
/// <param name="ResetsAt">When the window resets. Readings of one week agree on it to within minutes.</param>
/// <param name="Rejected">The plan refused a request at this reading: the account is limited until <paramref name="ResetsAt"/>.</param>
/// <param name="ObservedAt">When the reporter read it; null means now.</param>
/// <param name="Scope">The model family of a <see cref="MeterWindow.SevenDayScoped"/> reading.</param>
public sealed record MeterReading(
    MeterWindow Window, double Utilization, DateTimeOffset ResetsAt, bool Rejected = false, DateTimeOffset? ObservedAt = null, string? Scope = null);

/// <summary>What a consumer used, as it reports it.</summary>
/// <param name="Consumer">A consumer in the policy, or the owner.</param>
/// <param name="CostUsd">Cost in dollars at list price per model. For a <paramref name="Cumulative"/> report only the total counts.</param>
/// <param name="LeaseId">The lease from <see cref="Admission.LeaseId"/>; closes it.</param>
/// <param name="SessionId">A session whose reports are running totals.</param>
/// <param name="Cumulative">The costs are the session's running total so far; only the increase over the highest total seen counts.</param>
/// <param name="Route">Which credential the job ran on.</param>
public sealed record UsageReport(
    string Consumer, IReadOnlyDictionary<string, double> CostUsd, string? LeaseId = null, string? SessionId = null, bool Cumulative = false, UsageRoute Route = UsageRoute.Plan);

/// <summary>A request to start a job.</summary>
/// <param name="Consumer">A consumer in the policy, or the owner.</param>
/// <param name="EstimateUsd">What the job will cost at list price; null uses the consumer's median recent job, or 0.</param>
public sealed record AdmitRequest(string Consumer, double? EstimateUsd = null);

/// <summary>The answer to an <see cref="AdmitRequest"/>.</summary>
/// <param name="Decision">What the caller must do. Always <see cref="Decision.Allow"/> in <see cref="PaceMode.Observe"/>.</param>
/// <param name="Would">What <see cref="PaceMode.Enforce"/> would answer.</param>
/// <param name="LeaseId">Present when the decision is <see cref="Decision.Allow"/>: report with it to close the lease.</param>
/// <param name="LeaseTtl">How long the reservation lasts without a report.</param>
/// <param name="Status">The consumer's standing when it asked, before this job's reservation.</param>
/// <param name="RetryAfter">When <see cref="Would"/> is <see cref="Decision.Hold"/>: the earliest sensible retry.</param>
public sealed record Admission(
    Decision Decision, Decision Would, string? LeaseId, TimeSpan LeaseTtl, ConsumerStatus Status, TimeSpan? RetryAfter);

/// <summary>A consumer's standing. Used, reserved, allowance and remaining are fractions of the week's meter.</summary>
/// <param name="Name">The consumer.</param>
/// <param name="Share">Its share of the week, 0 to 1.</param>
/// <param name="Used">Attributed use this week.</param>
/// <param name="Reserved">Open leases, converted from dollars at the week's meter-per-dollar rate.</param>
/// <param name="Allowance">Share × elapsed ÷ 7 days: the pace line.</param>
/// <param name="Remaining">Allowance minus used minus reserved, not below 0.</param>
/// <param name="State">The consumer's state.</param>
/// <param name="OpenLeases">Leases not yet reported or expired.</param>
/// <param name="WeightedUsd">Reported use this week at list price, model-weighted.</param>
/// <param name="ApiUsd">Reported use on the API route this week.</param>
/// <param name="Estimator">Which estimate <paramref name="Used"/> is.</param>
public sealed record ConsumerStatus(
    string Name, double Share, double Used, double Reserved, double Allowance, double Remaining, PaceState State,
    int OpenLeases, double WeightedUsd, double ApiUsd, Estimator Estimator);

/// <summary>The account as a whole.</summary>
/// <param name="State">The account's state.</param>
/// <param name="Utilization">The weekly meter, null before the first reading.</param>
/// <param name="ResetsAt">The week's reset, null before the first reading.</param>
/// <param name="Elapsed">How much of the week has passed, 0 to 1.</param>
/// <param name="PaceLine">Total share × elapsed: where the meter would be on an even burn.</param>
/// <param name="Untracked">Meter rise no reported use accounts for, this week.</param>
/// <param name="MeterAge">Age of the newest reading, null before the first.</param>
/// <param name="LimitedUntil">Set while <paramref name="State"/> is limited.</param>
public sealed record AccountStatus(
    PaceState State, double? Utilization, DateTimeOffset? ResetsAt, double Elapsed, double PaceLine, double Untracked,
    TimeSpan? MeterAge, DateTimeOffset? LimitedUntil);

/// <summary>Everything the ledger knows now.</summary>
/// <param name="Account">The account.</param>
/// <param name="Consumers">Each consumer in the policy, and the owner, by name.</param>
public sealed record LedgerStatus(AccountStatus Account, IReadOnlyDictionary<string, ConsumerStatus> Consumers);

/// <summary>Something worth an audit line.</summary>
/// <param name="At">When.</param>
/// <param name="Kind"><c>lease_expired</c> (a job never reported), <c>limited</c> or <c>week_started</c>.</param>
/// <param name="Consumer">The consumer concerned, if any.</param>
/// <param name="Detail">A short human-readable detail.</param>
public sealed record LedgerEvent(DateTimeOffset At, string Kind, string? Consumer, string Detail);

/// <summary>Raised for a consumer the policy does not list.</summary>
public sealed class UnknownConsumerException(string consumer) : ArgumentException($"Unknown consumer '{consumer}'.", nameof(consumer))
{
    /// <summary>The name that was not found.</summary>
    public string Consumer { get; } = consumer;
}
