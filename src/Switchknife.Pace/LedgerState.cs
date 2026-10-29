namespace Switchknife.Pace;

/// <summary>
/// Everything the ledger remembers, as plain data so a store can write it down and read it back. The shape may grow in a
/// minor version; treat it as opaque and write it with <see cref="Ledger.Snapshot"/>.
/// </summary>
public sealed class LedgerState
{
    /// <summary>The newest reading per meter, keyed <c>FiveHour</c>, <c>SevenDay</c> or <c>SevenDayScoped:&lt;scope&gt;</c>.</summary>
    public Dictionary<string, MeterReading> Readings { get; init; } = [];

    /// <summary>The current week, null before the first weekly reading and the first report.</summary>
    public WeekState? Week { get; set; }

    /// <summary>Finished weeks, newest last, at most <see cref="Ledger.HistoryWeeks"/>.</summary>
    public List<WeekSummary> History { get; init; } = [];

    /// <summary>The highest running total seen per session id.</summary>
    public Dictionary<string, double> Sessions { get; init; } = [];

    /// <summary>Open leases by id.</summary>
    public Dictionary<string, LeaseState> Leases { get; init; } = [];

    /// <summary>The last lease number issued.</summary>
    public long LeaseSequence { get; set; }

    /// <summary>The model-weighted cost of each consumer's recent jobs, oldest first.</summary>
    public Dictionary<string, List<double>> JobCosts { get; init; } = [];

    /// <summary>The account is limited until this time, if it was ever rejected.</summary>
    public DateTimeOffset? LimitedUntil { get; set; }

    /// <summary>The last fitted k of a finished week, used until the current week has enough quiet-hour use of its own.</summary>
    public double? PriorK { get; set; }

    /// <summary>When the newest meter reading was observed.</summary>
    public DateTimeOffset? LastMeterAt { get; set; }

    internal LedgerState Clone()
    {
        var copy = new LedgerState
        {
            Week = Week?.Clone(),
            LeaseSequence = LeaseSequence,
            LimitedUntil = LimitedUntil,
            PriorK = PriorK,
            LastMeterAt = LastMeterAt,
        };
        foreach (var (k, v) in Readings) copy.Readings[k] = v;
        foreach (var w in History) copy.History.Add(w with { Used = new Dictionary<string, double>(w.Used) });
        foreach (var (k, v) in Sessions) copy.Sessions[k] = v;
        foreach (var (k, v) in Leases) copy.Leases[k] = v with { };
        foreach (var (k, v) in JobCosts) copy.JobCosts[k] = [.. v];
        return copy;
    }
}

/// <summary>One weekly window, keyed by the meter's own reset time.</summary>
public sealed class WeekState
{
    /// <summary>When the week resets; null until a weekly reading arrives (reports that came first wait for it).</summary>
    public DateTimeOffset? ResetsAt { get; set; }

    /// <summary>The weekly meter at the first reading of this week.</summary>
    public double Baseline { get; set; }

    /// <summary>The meter at the last reading that moved it.</summary>
    public double Utilization { get; set; }

    /// <summary>Consumers' reported use this week, model-weighted dollars at list price.</summary>
    public Dictionary<string, double> Weighted { get; init; } = [];

    /// <summary>Meter rise attributed to each consumer by splitting each rise over the use reported since the last rise.</summary>
    public Dictionary<string, double> Apportioned { get; init; } = [];

    /// <summary>Model-weighted dollars reported since the meter last rose.</summary>
    public Dictionary<string, double> SinceRise { get; init; } = [];

    /// <summary>Use on the API route this week, dollars.</summary>
    public Dictionary<string, double> ApiUsd { get; init; } = [];

    /// <summary>When the meter last rose (or the week was first read).</summary>
    public DateTimeOffset? LastRiseAt { get; set; }

    /// <summary>Meter rise over intervals that began and ended in quiet hours.</summary>
    public double QuietRise { get; set; }

    /// <summary>Model-weighted dollars reported in those intervals.</summary>
    public double QuietUse { get; set; }

    /// <summary>Meter rise with no reported use before it.</summary>
    public double Untracked { get; set; }

    internal WeekState Clone() => new()
    {
        ResetsAt = ResetsAt,
        Baseline = Baseline,
        Utilization = Utilization,
        Untracked = Untracked,
        LastRiseAt = LastRiseAt,
        QuietRise = QuietRise,
        QuietUse = QuietUse,
        Weighted = new(Weighted),
        Apportioned = new(Apportioned),
        SinceRise = new(SinceRise),
        ApiUsd = new(ApiUsd),
    };
}

/// <summary>A finished week.</summary>
/// <param name="ResetsAt">When it reset.</param>
/// <param name="Utilization">The meter at its end.</param>
/// <param name="Used">Attributed use per consumer, as fractions of the week.</param>
/// <param name="Untracked">Meter rise no reported use accounts for.</param>
public sealed record WeekSummary(DateTimeOffset ResetsAt, double Utilization, Dictionary<string, double> Used, double Untracked);

/// <summary>An admitted job's reservation.</summary>
/// <param name="Consumer">Who asked.</param>
/// <param name="EstimateUsd">The reserved cost at list price.</param>
/// <param name="OpenedAt">When it was admitted.</param>
/// <param name="ExpiresAt">When it is released and logged as unreported.</param>
public sealed record LeaseState(string Consumer, double EstimateUsd, DateTimeOffset OpenedAt, DateTimeOffset ExpiresAt);
