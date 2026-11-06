# Journal event stream

Status: accepted · Issue: Egoushka/switchknife#59 · Decision: `docs/adr/0008-journal-event-stream.md`

`Switchknife.Journal` already appends events per flow and reads them back per flow. A consumer that watches many flows (a dashboard, a projection, a "what happened since N" reader) needs one ordered stream over every flow, a way to narrow it by tag, appends that are safe to repeat, and a wake-up so it does not poll blind.

## Scope

- Read every flow's events in `seq` order, optionally only those with one `tag`.
- A `tag` column on events, indexed.
- Keyed append: `(flow, key)` is unique; a repeat returns the first `seq` and stores nothing. A batch form commits as one transaction.
- `IEventSignal`: a reader awaits new committed events.
- On SQLite (`SqliteStepStore`) and Postgres (`PgStepStore`), the same tests against both.

Out of scope: a cross-process signal (Postgres `LISTEN`/`NOTIFY`), the gapless per-room sequence of ADR 0007, deleting single events, hash chains.

## Contract

| Name | Shape |
|---|---|
| `NewEvent(Kind, Payload, Key = null, Tag = null)` | One event to append. `Key` makes the append idempotent inside its flow. |
| `IEventFeed` | Implemented by a store that has the stream: `AppendEventsAsync(flow, events, ct)`, `ReadAllEventsAsync(after, limit, tag, ct)`, `Signal`. |
| `IEventSignal.WaitAsync(after, ct)` | Completes with the highest committed `seq` this signal has seen, once it is greater than `after`; at once when it already is. |
| `StepJournal.AppendEventAsync(flow, NewEvent, ct)`, `AppendEventsAsync(flow, events, ct)`, `ReadAllEventsAsync(after, limit, tag, ct)`, `Signal` | The facade. Each throws `NotSupportedException` when the store is not an `IEventFeed`. |
| `JournalEvent.Tag` | `init` property, null when the event has none. Read by every read, per flow or global. |

Rules:

1. `ReadAllEventsAsync` returns events of every flow, `seq` ascending, `seq > after`, at most `limit`. With `tag`, only events whose tag equals it.
2. A key is unique per flow: the same key in two flows is two events. A repeat returns the first event's `seq`, stores nothing and raises no signal.
3. A batch is one transaction. An invalid event or a failure stores none of the batch and raises no signal. A batch whose every key already exists stores nothing and raises no signal. A key repeated inside one batch counts as a repeat.
4. `AppendEventsAsync` returns one `seq` per input event, in input order; for a repeat, the earlier event's `seq`.
5. The signal is raised once per committing append, after the commit returns; it is never raised before the rows are visible to another connection. It carries no events: the reader reads, then waits with the last `seq` it saw.
6. The signal is in-process: it reports commits made through the same store instance. A reader in another process polls (`ReadAllEventsAsync` on a timer).
7. `seq` is allocated before commit. With concurrent writers on Postgres a reader that resumes after N can miss a row committed later with a lower number (ADR 0007 names this). This spec does not change that; the README says so.

Storage:

- SQLite: schema version 2. `events` gains `tag TEXT` and `key TEXT`; index `events_by_tag (tag, seq)`; unique index `events_by_key (flow, key) WHERE key IS NOT NULL`. A version-1 file upgrades on open; a version-0 file is created at 2. An older library refuses a version-2 file (it already refuses newer versions).
- Postgres: `Migrations/0002_event_stream.sql`, idempotent, adds the same columns and indexes to `step_events`. `PgStepStoreSchema.Sql` returns every embedded migration in name order.

## Acceptance checks

Machine-run in the normal `dotnet test`. The Postgres side runs on `Switchknife.Testing.Postgres` (a container; skipped without Docker outside CI, as the existing Postgres tests are). `EventStreamTests` is one shared body (`tests/Switchknife.Journal.Tests/EventStreamContract.cs`), run once per store.

| Check | Test | Passes when |
|---|---|---|
| E1 | `ReadAllEventsAsync_returns_the_events_of_every_flow_in_seq_order` | Events appended to three flows, interleaved, come back in append order with ascending `seq`; `after` and `limit` page through them. |
| E2 | `A_repeat_append_with_the_same_flow_and_key_returns_the_first_seq_and_stores_nothing` | Second call returns the first `seq`; the stream holds one event; the same key in another flow is a new event. |
| E3 | `A_batch_of_500_is_one_transaction_and_one_signal` | 500 events commit; one signal; a reader sees all 500 at the moment it wakes. |
| E4 | `A_failing_batch_stores_nothing_and_raises_no_signal` | A batch whose last event is invalid leaves the stream empty and a waiter pending. |
| E5 | `A_repeated_batch_stores_nothing_and_raises_no_signal` | Replaying the 500 returns the same 500 `seq`s; count is still 500; no signal. |
| E6 | `A_reader_awaiting_the_signal_wakes_after_the_commit_never_before` | A pending `WaitAsync` is still pending before the append returns control; it completes after; a connection that is not the writer counts every appended row at that moment. A waiter with `after` below the latest `seq` completes at once. A cancelled wait throws `OperationCanceledException`. |
| E7 | `A_tag_filter_returns_only_that_tags_events` | Events tagged `a`, `b` and untagged: filtering `a` returns only `a` in `seq` order; an unknown tag returns none; the per-flow read exposes `Tag`. |
| E8 | `The_tag_read_uses_the_tag_index` | The plan of the tag read (SQLite `EXPLAIN QUERY PLAN`; Postgres `EXPLAIN` after `ANALYZE`, planner settings untouched) over 2000 rows in 40 tags names `events_by_tag` / `step_events_by_tag`. |
| E9 | `A_version_1_file_upgrades_on_open_and_keeps_its_events` | A file created with the version-1 schema and one event opens, keeps the event, accepts a keyed append. (SQLite test only.) |
| E10 | `The_schema_script_runs_twice_without_error_and_carries_the_event_stream_columns`, `Two_stores_appending_the_same_key_at_once_store_one_event` | `PgStepStoreSchema.Sql` applied again raises nothing and the columns exist; 40 concurrent appends of one key from two stores store one event. (Postgres tests only.) |
| E11 | AOT smoke | `samples/Switchknife.AotSmoke` Journal cases append keyed events, read by tag, and see a signal, published with Native AOT. |
| Gate | | `dotnet build -c Release -warnaserror`, `dotnet test`, `dotnet format --verify-no-changes`, `agent-checks check` pass. |
