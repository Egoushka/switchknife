# 4. Switchknife.Pace: a ledger with no I/O

Status: accepted

## Context

subgate ([Egoushka/subgate](https://github.com/Egoushka/subgate), ADR 0004 and 0005 there) paces each program's weekly share of one AI subscription. The pace and attribution logic is the part nobody else has, and other programs (an orchestrator's admission check, a simulation, tests) should use it without subgate's service.

## Decision

- One package, `Switchknife.Pace`, with one stateful type, `Ledger`, and plain records around it. The ledger does no I/O and has no dependency beyond the base library; the clock is a `TimeProvider`.
- **State is data.** `Ledger.Snapshot()` returns a `LedgerState` of plain collections; the constructor takes one. The package ships no serializer (no reflection JSON in an AOT library): the host writes it with its own source-generated context.
- **One lock.** Every public member takes one lock; the ledger is small and called a few times per job.
- **The week is the meter's reset.** Readings whose reset times differ by less than five minutes belong to one week (reporters round differently); a later reset starts a new week and keeps the old one (eight kept). A reading of an earlier week is ignored.
- **Used is attributed, in fractions of the week.** A rise in the weekly meter is split over the model-weighted cost consumers reported since the last rise; a rise with no reported use is untracked. This is exact when everything is reported. The calibrated estimate for the rest arrives in the next change (hook: `Estimator`).
- **Reservations convert dollars to meter points** at the week's apportioned meter-per-dollar rate; until the first apportioned rise the rate is 0 and a lease reserves nothing.
- **`Observe` mode never denies.** `Admission.Decision` is always `Allow`; `Would` is what `Enforce` answers. The owner is always allowed and takes no lease. A non-allow answer opens no lease.
- **States** are `Ok`, `OverPace`, `Limited`, `Stale`, checked in the order limited, stale, over pace. Before any weekly reading there is no pace line and the state is `Ok`.
- **Policy validation lives with the policy** (`Policy.Validate`), so every host rejects the same files: shares in 0 to 1 summing to at most 1, an action of `hold`, `api` or `cheap`, no action on the owner.

## Consequences

- A host owns persistence and transport; the engine is easy to test with a fake clock and to run in a simulation.
- `LedgerState` is public so a store can persist it; its shape may grow in a minor version, so hosts treat it as opaque.
- The reporters of a meter (a client's rate-limit event, a usage report) are a provider concern and stay out of this package.

## Reopen if

The lock becomes a bottleneck (it will not at a ledger's rate), or a second host needs a different state granularity than one snapshot.
