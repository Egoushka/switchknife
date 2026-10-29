using System.Globalization;

namespace Switchknife.Pace;

/// <summary>What <see cref="Ledger.Report"/> counted.</summary>
/// <param name="WeightedUsd">The model-weighted cost added to the consumer's week; 0 for a repeat of a session total.</param>
/// <param name="LeaseClosed">The report named an open lease of the same consumer and closed it.</param>
public sealed record UsageResult(double WeightedUsd, bool LeaseClosed);

/// <summary>
/// The pacing engine for one subscription: the plan's meter, each consumer's pace line, leases and states. It does no I/O and
/// reads time only from the <see cref="TimeProvider"/> it is given. All members are safe to call from several threads.
/// </summary>
public sealed class Ledger
{
    /// <summary>Finished weeks kept in <see cref="LedgerState.History"/>.</summary>
    public const int HistoryWeeks = 8;

    private const int JobCostWindow = 50;
    private const int MaxEvents = 1000;
    private const double Epsilon = 1e-9;
    private const double UntrackedStep = 0.01;
    private const double MinFitUsd = 1.0;
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly TimeSpan WeekTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinRetry = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Policy _policy;
    private readonly TimeProvider _clock;
    private readonly LedgerState _s;
    private readonly List<LedgerEvent> _events = [];

    /// <summary>Starts empty, or from a state written by <see cref="Snapshot"/>.</summary>
    /// <param name="policy">The rules; it must have no <see cref="Policy.Validate"/> problems.</param>
    /// <param name="clock">The source of time; tests pass a fake.</param>
    /// <param name="state">A state to continue from.</param>
    public Ledger(Policy policy, TimeProvider? clock = null, LedgerState? state = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Validate() is { Count: > 0 } problems)
        {
            throw new ArgumentException("Invalid policy: " + string.Join("; ", problems), nameof(policy));
        }

        _policy = policy;
        _clock = clock ?? TimeProvider.System;
        _s = state?.Clone() ?? new LedgerState();
    }

    /// <summary>Takes in a reading of the plan's meter. A weekly reading with a later reset starts a new week.</summary>
    public void Observe(MeterReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (double.IsNaN(reading.Utilization) || reading.Utilization < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reading), "Utilization must be 0 or more.");
        }

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Expire(now);
            var at = reading.ObservedAt ?? now;
            var stored = reading with { ObservedAt = at, Utilization = Math.Min(reading.Utilization, 1) };
            var key = reading.Window == MeterWindow.SevenDayScoped ? $"{reading.Window}:{reading.Scope}" : reading.Window.ToString();
            if (!_s.Readings.TryGetValue(key, out var previous) || at >= previous.ObservedAt)
            {
                _s.Readings[key] = stored;
            }

            if (_s.LastMeterAt is not { } last || at > last)
            {
                _s.LastMeterAt = at;
            }

            if (reading.Rejected && reading.ResetsAt > now && (_s.LimitedUntil is not { } until || reading.ResetsAt > until))
            {
                _s.LimitedUntil = reading.ResetsAt;
                AddEvent(now, "limited", null, string.Create(CultureInfo.InvariantCulture, $"{reading.Window} rejected until {reading.ResetsAt:O}"));
            }

            if (reading.Window == MeterWindow.SevenDay)
            {
                ApplyWeekly(stored, now);
            }
        }
    }

    /// <summary>Counts what a consumer used and closes the lease it names.</summary>
    /// <exception cref="UnknownConsumerException">The consumer is not in the policy and is not the owner.</exception>
    public UsageResult Report(UsageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(report.CostUsd);
        foreach (var cost in report.CostUsd.Values)
        {
            if (!double.IsFinite(cost) || cost < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(report), "A cost must be 0 or more.");
            }
        }

        lock (_gate)
        {
            RequireKnown(report.Consumer);
            var now = _clock.GetUtcNow();
            Expire(now);

            var total = report.CostUsd.Sum(kv => kv.Value * _policy.Weight(kv.Key));
            var delta = total;
            if (report.Cumulative && report.SessionId is { } session)
            {
                var seen = _s.Sessions.GetValueOrDefault(session);
                delta = Math.Max(0, total - seen);
                _s.Sessions[session] = Math.Max(seen, total);
            }

            var week = _s.Week ??= new WeekState();
            if (report.Route == UsageRoute.Api)
            {
                Add(week.ApiUsd, report.Consumer, delta);
            }
            else
            {
                Add(week.Weighted, report.Consumer, delta);
                Add(week.SinceRise, report.Consumer, delta);
            }

            if (!report.Cumulative && delta > 0)
            {
                var costs = _s.JobCosts.TryGetValue(report.Consumer, out var c) ? c : _s.JobCosts[report.Consumer] = [];
                costs.Add(delta);
                if (costs.Count > JobCostWindow)
                {
                    costs.RemoveAt(0);
                }
            }

            var closed = report.LeaseId is { } id && _s.Leases.TryGetValue(id, out var lease) && lease.Consumer == report.Consumer
                && _s.Leases.Remove(id);
            return new UsageResult(delta, closed);
        }
    }

    /// <summary>
    /// A job asks to start. In <see cref="PaceMode.Observe"/> the decision is always <see cref="Decision.Allow"/> and
    /// <see cref="Admission.Would"/> says what <see cref="PaceMode.Enforce"/> would answer; the owner is always allowed.
    /// An allowed job reserves its estimate until it reports or the lease expires.
    /// </summary>
    /// <exception cref="UnknownConsumerException">The consumer is not in the policy and is not the owner.</exception>
    public Admission Admit(AdmitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EstimateUsd is { } e && (!double.IsFinite(e) || e < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The estimate must be 0 or more.");
        }

        lock (_gate)
        {
            RequireKnown(request.Consumer);
            var now = _clock.GetUtcNow();
            Expire(now);
            var ctx = Context.Of(this, now);
            var status = ctx.Consumer(request.Consumer);

            if (request.Consumer == _policy.Owner)
            {
                return new Admission(Decision.Allow, Decision.Allow, null, _policy.LeaseTtl, status, null);
            }

            var (would, retry) = Evaluate(request.Consumer, status, ctx, now);
            var decision = _policy.Mode == PaceMode.Enforce ? would : Decision.Allow;
            string? leaseId = null;
            if (decision == Decision.Allow)
            {
                leaseId = string.Create(CultureInfo.InvariantCulture, $"l{++_s.LeaseSequence:D8}");
                _s.Leases[leaseId] = new LeaseState(request.Consumer, request.EstimateUsd ?? Median(request.Consumer), now, now + _policy.LeaseTtl);
            }

            return new Admission(decision, would, leaseId, _policy.LeaseTtl, status, would == Decision.Hold ? retry : null);
        }
    }

    /// <summary>Every consumer's standing and the account's.</summary>
    public LedgerStatus Status()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Expire(now);
            var ctx = Context.Of(this, now);
            var names = _policy.Consumers.Keys.Append(_policy.Owner).Distinct();
            return new LedgerStatus(ctx.Account(), names.ToDictionary(n => n, ctx.Consumer));
        }
    }

    /// <summary>A copy of everything the ledger remembers, for a store to write; pass it to the constructor to continue from it.</summary>
    public LedgerState Snapshot()
    {
        lock (_gate)
        {
            return _s.Clone();
        }
    }

    /// <summary>Takes the events since the last call: jobs that never reported, rejections, new weeks.</summary>
    public IReadOnlyList<LedgerEvent> DrainEvents()
    {
        lock (_gate)
        {
            Expire(_clock.GetUtcNow());
            var drained = _events.ToArray();
            _events.Clear();
            return drained;
        }
    }

    private void ApplyWeekly(MeterReading r, DateTimeOffset now)
    {
        var week = _s.Week;
        if (week?.ResetsAt is null)
        {
            week ??= _s.Week = new WeekState();
            Adopt(week, r);
        }
        else if (r.ResetsAt > week.ResetsAt + WeekTolerance)
        {
            _s.PriorK = FittedK(week) ?? _s.PriorK;
            _s.History.Add(new WeekSummary(week.ResetsAt.Value, week.Utilization, UsedBy(week), week.Untracked));
            if (_s.History.Count > HistoryWeeks)
            {
                _s.History.RemoveAt(0);
            }

            _s.Week = week = new WeekState();
            Adopt(week, r);
            AddEvent(now, "week_started", null, string.Create(CultureInfo.InvariantCulture, $"resets {r.ResetsAt:O}"));
        }
        else if (r.ResetsAt >= week.ResetsAt - WeekTolerance && r.Utilization > week.Utilization + Epsilon)
        {
            var rise = r.Utilization - week.Utilization;
            week.Utilization = r.Utilization;
            var used = week.SinceRise.Values.Sum();
            var at = r.ObservedAt ?? now;
            if (week.LastRiseAt is { } from && _policy.Quiet.ContainsInterval(from, at))
            {
                week.QuietRise += rise;
                week.QuietUse += used;
            }

            week.LastRiseAt = at;
            if (used > 0)
            {
                foreach (var (consumer, amount) in week.SinceRise)
                {
                    Add(week.Apportioned, consumer, rise * amount / used);
                }

                week.SinceRise.Clear();
            }
            else
            {
                week.Untracked += rise;
            }
        }
    }

    private static void Adopt(WeekState week, MeterReading r)
    {
        week.ResetsAt = r.ResetsAt;
        week.Baseline = r.Utilization;
        week.Utilization = r.Utilization;
        week.LastRiseAt = r.ObservedAt;
    }

    /// <summary>k from the quiet-hour intervals so far, once they hold enough reported use to mean something.</summary>
    private static double? FittedK(WeekState week) => week.QuietUse >= MinFitUsd && week.QuietRise > 0 ? week.QuietRise / week.QuietUse : null;

    private static Dictionary<string, double> UsedBy(WeekState week) => new(week.Apportioned);

    private (Decision Would, TimeSpan? Retry) Evaluate(string name, ConsumerStatus status, Context ctx, DateTimeOffset now)
    {
        var policy = _policy.Consumers.GetValueOrDefault(name);
        switch (status.State)
        {
            case PaceState.Limited:
                return (Decision.Hold, Max(ctx.LimitedUntil!.Value - now, MinRetry));
            case PaceState.Stale:
                var onStale = policy?.OnStale ?? Decision.Hold;
                return (onStale, onStale == Decision.Hold ? StaleRetry : null);
            case PaceState.OverPace:
                var action = policy?.Action ?? Decision.Hold;
                return (action, action == Decision.Hold ? ctx.RetryAfterOverPace(status) : null);
            default:
                return (Decision.Allow, null);
        }
    }

    private double Median(string consumer)
    {
        if (!_s.JobCosts.TryGetValue(consumer, out var costs) || costs.Count == 0)
        {
            return 0;
        }

        var sorted = costs.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private void Expire(DateTimeOffset now)
    {
        List<string>? gone = null;
        foreach (var (id, lease) in _s.Leases)
        {
            if (lease.ExpiresAt <= now)
            {
                (gone ??= []).Add(id);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (var id in gone)
        {
            var lease = _s.Leases[id];
            _s.Leases.Remove(id);
            AddEvent(now, "lease_expired", lease.Consumer, string.Create(CultureInfo.InvariantCulture, $"{id} reserved ${lease.EstimateUsd:0.####} and never reported"));
        }
    }

    private void AddEvent(DateTimeOffset at, string kind, string? consumer, string detail)
    {
        _events.Add(new LedgerEvent(at, kind, consumer, detail));
        if (_events.Count > MaxEvents)
        {
            _events.RemoveAt(0);
        }
    }

    private void RequireKnown(string consumer)
    {
        if (consumer != _policy.Owner && !_policy.Consumers.ContainsKey(consumer))
        {
            throw new UnknownConsumerException(consumer);
        }
    }

    private static void Add(Dictionary<string, double> map, string key, double amount) => map[key] = map.GetValueOrDefault(key) + amount;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>The numbers one call needs, computed once.</summary>
    private sealed class Context
    {
        private readonly Ledger _l;
        private readonly DateTimeOffset _now;
        private readonly WeekState? _week;
        private readonly double _rate;
        private readonly double? _k;
        private readonly bool _calibrated;
        private readonly PaceState? _accountState;

        private Context(Ledger l, DateTimeOffset now)
        {
            _l = l;
            _now = now;
            _week = l._s.Week;
            Elapsed = _week?.ResetsAt is null ? 0 : Math.Clamp((now - (_week.ResetsAt!.Value - Week)) / Week, 0, 1);
            PaceLine = Elapsed * l._policy.TotalShare;
            MeterAge = l._s.LastMeterAt is { } at ? Max(now - at, TimeSpan.Zero) : null;
            LimitedUntil = l._s.LimitedUntil is { } until && until > now ? until : null;
            _k = (_week is null ? null : FittedK(_week)) ?? l._s.PriorK;
            _calibrated = _week is not null && _k is { } k
                && _week.Utilization - _week.Baseline - (k * _week.Weighted.Values.Sum()) > UntrackedStep;
            _rate = _k ?? (_week is null ? 0 : MeterPerDollar(_week));
            _accountState = LimitedUntil is not null ? PaceState.Limited
                : MeterAge > l._policy.StaleAfter ? PaceState.Stale
                : null;
        }

        public double Elapsed { get; }

        public double PaceLine { get; }

        public TimeSpan? MeterAge { get; }

        public DateTimeOffset? LimitedUntil { get; }

        private bool Paced => _week?.ResetsAt is not null;

        private bool AccountOver => Paced && _week!.Utilization > PaceLine + Epsilon;

        public static Context Of(Ledger l, DateTimeOffset now) => new(l, now);

        public AccountStatus Account() => new(
            _accountState ?? (AccountOver ? PaceState.OverPace : PaceState.Ok),
            Paced ? _week!.Utilization : null, _week?.ResetsAt, Elapsed, PaceLine, Untracked(), MeterAge, LimitedUntil, _k);

        private double Untracked() => _week is null ? 0
            : _calibrated ? Math.Max(0, _week.Utilization - _week.Baseline - (_k!.Value * _week.Weighted.Values.Sum()))
            : _week.Untracked;

        public ConsumerStatus Consumer(string name)
        {
            var share = _l._policy.Consumers.TryGetValue(name, out var p) ? p.Share : 0;
            var apportioned = _week?.Apportioned.GetValueOrDefault(name) ?? 0;
            double? calibrated = _k is { } k && _week is not null ? k * _week.Weighted.GetValueOrDefault(name) : null;
            var used = _calibrated ? calibrated!.Value : apportioned;
            var leases = _l._s.Leases.Values.Where(x => x.Consumer == name).ToArray();
            var reserved = leases.Sum(x => x.EstimateUsd) * _rate;
            var allowance = share * Elapsed;
            var state = _accountState
                ?? (Paced && (used + reserved > allowance + Epsilon || AccountOver) ? PaceState.OverPace : PaceState.Ok);
            return new ConsumerStatus(
                name, share, used, reserved, allowance, Math.Max(0, allowance - used - reserved), state, leases.Length,
                _week?.Weighted.GetValueOrDefault(name) ?? 0, _week?.ApiUsd.GetValueOrDefault(name) ?? 0,
                _calibrated ? Estimator.Calibrated : Estimator.Apportion, apportioned, calibrated);
        }

        public TimeSpan RetryAfterOverPace(ConsumerStatus s)
        {
            var untilReset = _week!.ResetsAt!.Value - _now;
            var wait = TimeSpan.Zero;
            var excess = s.Used + s.Reserved - s.Allowance;
            if (excess > Epsilon)
            {
                wait = s.Share > 0 ? Week * (excess / s.Share) : untilReset;
            }

            var total = _l._policy.TotalShare;
            if (AccountOver)
            {
                wait = Max(wait, total > 0 ? Week * ((_week.Utilization / total) - Elapsed) : untilReset);
            }

            return Max(wait > untilReset ? untilReset : wait, MinRetry);
        }

        /// <summary>Meter points per reported dollar this week; 0 until a rise has been apportioned.</summary>
        private static double MeterPerDollar(WeekState w)
        {
            var apportionedUsd = w.Weighted.Values.Sum() - w.SinceRise.Values.Sum();
            return apportionedUsd > Epsilon ? w.Apportioned.Values.Sum() / apportionedUsd : 0;
        }
    }
}
