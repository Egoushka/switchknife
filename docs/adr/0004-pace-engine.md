# 4. Switchknife.Pace: a ledger with no I/O

Status: accepted

## Context

subgate ([Egoushka/subgate](https://github.com/Egoushka/subgate), ADR 0004 and 0005 there) paces each program's weekly share of one AI subscription. The pace and attribution logic is the part nobody else has, and other programs (an orchestrator's admission check, a simulation, tests) should use it without subgate's service.

## Decision

- One package, `Switchknife.Pace`, with one stateful type, `Ledger`, and plain records around it. The ledger does no I/O and has no dependency beyond the base library; the clock is a `TimeProvider`.
- **State is data.** `Ledger.Snapshot()` returns a `LedgerState` of plain collections; the constructor takes one. The package ships no serializer (no reflection JSON in an AOT library): the host writes it with its own source-generated context.
- **One lock.** Every public member takes one lock; the ledger is small and called a few times per job.
- **The week is the meter's reset.** Readings whose reset times differ by less than five minutes belong to one week (reporters round differently); a later reset starts a new week and keeps the old one (eight kept). A reading of an earlier week is ignored.
- **Used is attributed, in fractions of the week**, by two estimators (subgate ADR 0005). *Apportioned*: a rise in the weekly meter is split over the model-weighted cost consumers reported since the last rise; a rise with no reported use is untracked. Exact when everything is reported. *Calibrated*: reported weighted use × k, with k = meter rise ÷ reported weighted use over intervals that began and ended in the policy's quiet hours, once at least one dollar of use is in them; a finished week's k is kept until the next week has its own. The ledger reports the apportioned estimate while k × all reported use is within one point (0.01) of the week's rise, and the calibrated one when untracked use shows. The simulation test fixes the target: worst consumer's p95 error ≤ 5 points of the week in every cell.
- **Reservations convert dollars to meter points** at k when known, else at the week's apportioned meter-per-dollar rate; before either exists the rate is 0 and a lease reserves nothing.
- **`Observe` mode never denies.** `Admission.Decision` is always `Allow`; `Would` is what `Enforce` answers. The owner is always allowed and takes no lease. A non-allow answer opens no lease.
- **States** are `Ok`, `OverPace`, `Limited`, `Stale`, checked in the order limited, stale, over pace. Before any weekly reading there is no pace line and the state is `Ok`.
- **Policy validation lives with the policy** (`Policy.Validate`), so every host rejects the same files: shares in 0 to 1 summing to at most 1, an action of `hold`, `api` or `cheap`, no action on the owner.

## Consequences

- A host owns persistence and transport; the engine is easy to test with a fake clock and to run in a simulation.
- `LedgerState` is public so a store can persist it; its shape may grow in a minor version, so hosts treat it as opaque.
- The reporters of a meter (a client's rate-limit event, a usage report) are a provider concern and stay out of this package.

## Reopen if

The lock becomes a bottleneck (it will not at a ledger's rate), or a second host needs a different state granularity than one snapshot.
