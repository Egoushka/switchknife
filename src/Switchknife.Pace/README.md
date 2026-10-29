# Switchknife.Pace

The pacing engine for one AI subscription that several programs share. It does no I/O: you feed it the plan's meter readings and what each program used, and ask it whether a program may start. Time comes from the `TimeProvider` you pass.

A subscription has one weekly allowance, shared by every program on the login. `Ledger` gives each consumer a **share** of the week and paces it: the **pace line** is `share × elapsed ÷ 7 days`, so unused allowance carries forward and nobody can spend the week on day one. The week is keyed by the meter's own reset time, never by a weekday.

```csharp
var policy = new Policy
{
    Consumers = new Dictionary<string, ConsumerPolicy>
    {
        ["nightly-review"] = new(Share: 0.25),
        ["owner"] = new(Share: 0.75),          // measured, never gated
    },
    Mode = PaceMode.Observe,                   // Enforce answers what Observe only says
};
var ledger = new Ledger(policy, TimeProvider.System);

ledger.Observe(new MeterReading(MeterWindow.SevenDay, utilization: 0.42, resetsAt));  // from the plan, 0 to 1

var admission = ledger.Admit(new AdmitRequest("nightly-review", EstimateUsd: 0.80));
// admission.Decision: Allow (always, in Observe)   admission.Would: Allow | Hold | Api | Cheap
// admission.LeaseId reserves the estimate until the job reports

ledger.Report(new UsageReport("nightly-review", new Dictionary<string, double> { ["model"] = 0.74 }, admission.LeaseId));
var status = ledger.Status();                  // per consumer: share, used, allowance, remaining, state
```

What it keeps:

- **States**, first match wins: `Limited` after any reading was `Rejected`, until its reset; `Stale` when the newest reading is older than `StaleAfter` (default 6 h); `OverPace` when a consumer's use plus open leases passes its pace line, or the account meter passes the total pace line; else `Ok`. Before any weekly reading a consumer is `Ok` and there is no pace line.
- **Leases.** An allowed job reserves its estimate (or the consumer's median recent job) until a report names its lease or `LeaseTtl` passes. An expired lease is released and logged as unreported (`DrainEvents`). Ten jobs admitted at once cannot all fit the same remaining share.
- **Attribution.** The meter is account-wide and moves in steps of one point, so a job's own effect is below its resolution. Two estimates are kept per consumer. *Apportioned*: each rise is split across consumers by the use they reported since the last rise; exact when every use is reported. *Calibrated*: reported weighted use × **k**, the meter rise per reported dollar, fitted only from intervals that began and ended in `Policy.Quiet` hours, when the owner is idle (default 00:00 to 08:00, local; a finished week's k carries over). `Used` is the apportioned estimate while reported use explains the meter (to within one point of the week), and the calibrated one when it does not; `Untracked` is what no reporter accounts for. In a simulated week the worst consumer's 95th-percentile error stays under 2 points of the week at every level of unreported owner use (`AttributionSimulationTests`).
- **Running totals.** A report with `Cumulative` and a `SessionId` counts only the increase over the highest total seen for that session, so a status line may repeat itself safely.
- **The owner** is the consumer named `Policy.Owner`: measured, never gated, never reserved.
- **Persistence.** `Snapshot()` returns plain data; pass it to the constructor to continue after a restart. Write it with your own source-generated JSON.

Thread-safe. No reflection, Native AOT compatible, no dependencies beyond the base library. Design: [ADR 0004](https://github.com/Egoushka/switchknife/blob/main/docs/adr/0004-pace-engine.md); the first consumer is [subgate](https://github.com/Egoushka/subgate).
