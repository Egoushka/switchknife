# 7. Rooms: a hash-chained event log in the Journal's event table, with memories as projections

- Status: proposed
- Date: 2026-11-01

## Conclusion

A room is a Journal flow. Its events live in `step_events`, extended by an additive migration with a per-room sequence, an actor, an occurrence time, refs, an undo kind, an idempotency key and a hash chain that covers each body's hash, not the body. Facts, the idea board, research rounds and Hindsight directives become projections rebuilt from that log. Agents read a room with exact filters only. Spec and checks: `docs/specs/2026-11-01-rooms.md`.

## Context

The owner's direction (2026-11-01): agents keep no memory of their own and act as hands; a log lets any agent rebuild what happened at any point, and every interaction is visible in rooms. One correction was accepted with it: knowledge can rewind, the world cannot, so each side effect records how it can be reversed (`revert`, `compensate`, `none`).

Constraints:

- **One raw layer, not a sixth memory.** Hindsight, Facts, Synapse and chronicle each capture on their own today. A log is worth building only if the others become views of it.
- **Exact reads.** ADR 0006 stopped agent-facing retrieval on the kit (recall@10 0.199 against 0.32). A brief must not depend on search.
- **Two consumers first.** Claude Code sessions and chargehand runs now; Nytka conversations validate the contract as the third.
- **No new store** (workspace rule D-39). A table in a database a host already runs is not a new store; a new database or container is.
- **Privacy.** Employer sessions never enter; secrets are masked before a write; the approvals shell's sharing modes apply.

What exists, with evidence:

| Part | State | Evidence |
|---|---|---|
| `step_events` | `seq` (global identity), `flow`, `kind`, `at`, `payload json`; index on `(flow, seq)` | `src/Switchknife.Journal.Postgres/Migrations/0001_steps.sql` |
| Reads after a sequence | `ReadEventsAsync(flow, after, limit)` | `PgStepStore.cs` |
| Deletion | `ForgetAsync(flow)` removes a flow's steps and events | `IStepStore.cs` |
| chargehand's journal | SQLite file in its state directory; batch flows forgotten when done | chargehand `DrivenJournal.cs` (`StepJournal.OpenAsync`), `TaskJournal.cs` (`ForgetAsync`) |
| Facts | Bi-temporal, idempotent episodes `(kind, ref, hash)`, `PurgeSourceAsync`, `VerifySlotAsync` | `src/Switchknife.Facts/README.md` |

Two properties of `step_events` matter. The global identity `seq` is allocated before commit, so with concurrent writers a reader that resumes "after N" can miss a row committed later with a lower number. And nothing stops deletion. Neither matters for chargehand's crash recovery; both matter for a log that agents rebuild their state from.

## Options

| Option | For | Against | Verdict |
|---|---|---|---|
| A. Envelope inside `payload`, no schema change | No migration for the columns | Filters by actor, time or ref become JSON expressions; the idempotency key still needs a unique expression index, so a migration anyway; purge must rewrite the JSON | Rejected: the same migration with worse queries |
| **B. Extend `step_events` with nullable room columns; `PgRoomLog` in `Switchknife.Journal.Postgres`; contract, adapters, brief and tools in a new `Switchknife.Rooms`** | One event table for step flows and rooms; existing flows unchanged; typed, indexed filters; a run moved to `PgStepStore` is readable by exact flow name | `Journal.Postgres` gains a second job (rooms beside steps) | **Chosen** |
| C. A separate `room_events` table | Clean ownership | Two event tables, so two raw layers, which is the problem being solved | Rejected |
| D. Git as the log: an orphan branch per room, a commit per event | Fork = branch, rewind = checkout, tamper evidence, diff and bisect for free | Slow appends, no filtered reads, a push per event | Kept as the export and snapshot format, not the store |
| E. A Rust single binary on SQLite (WAL, hash chain, MCP over stdio) | Runs on the laptop, the server and the phone with no dependencies | A second implementation before the first has a number | Deferred: built only if the C# ingest misses the latency line |

## Decision

1. **Storage.** Migration `0002_rooms.sql` in `Switchknife.Journal.Postgres` adds nullable columns to `step_events` (`room_seq`, `ts`, `actor`, `refs`, `undo`, `source_id`, `body_hash`, `prev_hash`, `hash`, `purged_at`), unique indexes on `(flow, room_seq)` and `(flow, source_id)` where not null, and a `rooms` table (key, fork parent and sequence, sharing mode). `IStepStore` does not change.
2. **Order.** Append takes a transaction-scoped advisory lock on the room key and assigns `room_seq` under it, so a room's sequence has no gaps and matches commit order. Readers page by `room_seq`, never by the global `seq`.
3. **Chain.** `hash = SHA-256` over a length-prefixed canonical envelope with refs sorted, ending in `body_hash` and `prev_hash`. Because the chain covers the body's hash, a body can be purged (privacy, retention) and the chain still verifies; an edit to a stored body or envelope does not.
4. **Deletion.** `ForgetAsync` throws on a room flow. A body leaves by `PurgeBodyAsync`; a whole room by `PurgeRoomAsync`, which also tells the projections.
5. **Forks.** A fork is a new room whose first event continues the parent's chain at the fork point. Nothing edits the past.
6. **Undo kind** is required on `side_effect` and refused elsewhere; the brief lists effects after T that are still in force, because a rewound brief does not rewind the world.
7. **Reads** filter by room, sequence, time, kind, actor and ref, capped by count with a `truncated` flag. No score, no embedding.
8. **Projections** read rooms in sequence order from a checkpoint table; each must rebuild to the same state it reached incrementally. The Facts projection uses source ref `<room>#<seq>` and the event hash as content hash, so Facts' own idempotency and purge apply unchanged.
9. **Privacy** runs before the write: employer fence (whole session from the first employer line on), sharing mode of the room, secret scan with masking before `body_hash`. A fence that cannot decide refuses.
10. **Capture is ingest, not instrumentation.** Slice 1 tails Claude Code transcripts off the tool path; chargehand events arrive by an adapter that copies them before a batch is forgotten.

## Consequences

- `Switchknife.Journal.Postgres` carries rooms beside steps; its README and CHANGELOG say so, and its tests must pass unchanged after the migration (spec M1).
- Hosts that already run `0001_steps.sql` run `0002_rooms.sql` in order; a host that never uses rooms may skip it.
- A room is append-only by construction and auditable by `VerifyAsync`; the cost is one advisory lock and one indexed read per appended batch.
- Bodies are stored after masking, so the log cannot answer what a masked secret was. That is intended.
- Hindsight's rebuild is UNKNOWN until its API is checked (slice 6). If its session retain job cannot read rooms, rooms are not the raw layer, and this ADR is amended rather than the claim kept.
- The chargehand claim in the design note ("joins without code") was wrong: its journal is a local SQLite file whose batch flows are forgotten. The adapter is slice 3.

## Check and stop

From the spec. **Check:** for 10 finished sessions, a fresh agent given only `room_brief` at point T answers 5 fixed questions at 80% or better (a starting value to calibrate), and 0 facts are re-asked in 3 of 3 continued tasks. **Stop:** the brief scores below the same sessions' `HANDOFF.md`, or ingest adds more than 100 ms p95 per tool call. On stop, transcripts stay as they are, slices 2 onward are not built, and this ADR records the numbers.
