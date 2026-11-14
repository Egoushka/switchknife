# Facts: supersession edges and interval queries

Proposals `switchknife/P01` and `switchknife/P02` of the toolpool research. Builds on `docs/adr/0001-facts-library.md`. Status: this file, then code.
P03 (extraction scored against a gold set) starts only after both land.

## What already exists

The schema holds everything these two need, so there is **no migration**: `facts.facts` has `valid` (when it was true), `recorded_at`, `retired_at`, `retired_reason` and `superseded_by`; `AsOfAsync` answers valid-time plus belief-time; `HistoryAsync` and `ExplainAsync` show the chain; search already skips rows retired at the query's belief time. A `one` predicate supersedes by rule on write.

What is missing:

1. A `many` fact (`likes`) can never be closed by naming its replacement; only retraction (no successor) retires it.
2. No one-call "what do we believe now".
3. Validity windows can only be asked "at instant T". "What was true while X" needs hand-written SQL.

## P01: supersession edges

- `SupersedeAsync(oldId, newId)` retires the live row `oldId` with reason `superseded`, `retired_at` = the store's clock, `superseded_by` = `newId`. Nothing is deleted. Returns `true` when it wrote, `false` when `oldId` was already superseded by `newId` (idempotent).
- Both rows must be live rows of this namespace about the same subject and the same predicate, and the predicate must be `many` (an event is never superseded; a `one` predicate supersedes by rule, and a hand-made edge would break purge's replay). Anything else throws `InvalidOperationException` (retired row, other subject, event, unknown id) or `ArgumentException` (same id).
- `CurrentAsync(subject, predicate?)` is "what do we believe now": `AsOfAsync` at the store's clock for both timelines.
- Search by default skips superseded facts (belief time defaults to now); `knownAt` before the supersession still returns it.

## P02: interval queries

`IntervalRelation` is Allen's thirteen relations of a fact's validity window to a query window, plus `Intersects` (any shared instant). Windows are half-open `[from, to)`; an event (a point `[t,t]`) is an instant: `Intersects` holds when the window contains it, the other relations compare it as a bound, and as the window of `WhileAsync` it is `[t, t+1µs)`; an unbounded end (`null`) compares as infinite, so two open ends are equal.

`QueryAsync(relation, from, to, subject?, predicate?, knownAt?)` and `WhileAsync(factId, relation = Intersects, knownAt?)` (the window is that fact's own window; the fact is excluded). Both respect belief time like `AsOfAsync`; `knownAt` defaults to now and a future instant is refused.

## Acceptance checks (tests, `tests/Switchknife.Facts.Tests/SupersessionTests.cs` and `IntervalTests.cs`)

| # | Check |
|---|---|
| S1 | Supersede a `many` fact; `AsOfAsync` at two belief times returns the old before and only the new after. |
| S2 | The old row is still in `HistoryAsync` with `RetiredReason = superseded`, `SupersededBy = new`, `RetiredAt` = the clock. Row count unchanged. |
| S3 | `ExplainAsync` on the old row shows the chain to the new one. |
| S4 | `SearchAsync` skips the superseded fact by default and returns it with `knownAt` before the supersession. |
| S5 | Superseding twice with the same pair returns `false`; a different successor, a retired row, another subject, another predicate, an event fact, a `one` fact and the same id are refused. |
| S6 | `CurrentAsync` returns the current belief; after a `one` rule-supersession it matches the new value. |
| I1 | Each of the 13 relations plus `Intersects` returns exactly the expected rows from a fixed set of windows. |
| I2 | Unbounded ends: a window open at the end is `Contains` a bounded later one and `Intersects` any later window. |
| I3 | `WhileAsync` returns the overlapping facts and not the fact itself. |
| I4 | `QueryAsync` results equal hand-written range SQL (`&&`, `<@`, bounds) on the same rows for three queries. |
| I6 | An event inside the window is returned by `Intersects`; `WhileAsync` on an event works. |
| I5 | Belief time: a retired row is excluded now and included at an earlier `knownAt`; a future `knownAt` throws. |

Consumers named in the proposals (Synapse forgetting pass, JARVIS `/why`, feed-eater) are not changed here.
