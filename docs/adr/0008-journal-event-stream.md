# 8. An event stream in Switchknife.Journal

Status: accepted

## Context

`Switchknife.Journal` events are per flow: `AppendEventAsync(flow, kind, payload)` and `ReadEventsAsync(flow, after, limit)`. Consumers that watch many flows have no ordered read across them, no filter, no safe retry of an append, and no wake-up. Spec and checks: `docs/specs/2026-11-06-journal-event-stream.md`. `journal/v0.2.0` is released with `IStepStore` public, so changes to the public surface must not break an existing implementer or caller.

## Decision

- The stream is a second interface, `IEventFeed`, implemented by `SqliteStepStore` and `PgStepStore`. `IStepStore` does not change: a third-party store keeps compiling. `StepJournal` throws `NotSupportedException` for the stream methods when its store is not an `IEventFeed`.
- New append and batch methods take a `NewEvent` record instead of adding optional parameters to `AppendEventAsync`: an optional parameter before `CancellationToken` changes the existing method's signature (a binary break), and `AppendEventAsync(flow, kind, payload, ct)` would stop compiling.
- `JournalEvent` gains an `init` property `Tag`, not a positional parameter, so its constructor and `Deconstruct` keep their shape.
- Idempotency is a unique index `(flow, key)` where `key` is not null, enforced by the database (`ON CONFLICT DO NOTHING`, then read the existing `seq`), not by a read-then-insert, so two processes on Postgres agree.
- `IEventSignal` is a hint with an after-sequence, not an event channel: `WaitAsync(after)` completes once a committed `seq` above `after` has been seen. The reader reads, then waits with the last `seq` it saw. In-process only; `EventSignal` is a public class so `Switchknife.Journal.Postgres` can use it.
- The Postgres columns go in `0002_event_stream.sql`. ADR 0007's rooms migration was named `0002_rooms.sql` and carries its own idempotency key (`source_id`); whoever builds rooms renumbers theirs to `0003` and can reuse `key`.

## Options not taken

- Add the methods to `IStepStore` with default interface implementations: no break for implementers, but a store that cannot do the stream would compile and fail at run time with nothing in its type to say so.
- Cross-process signal with `LISTEN`/`NOTIFY` now: `NOTIFY` is delivered at commit, so it fits rule 5, but it needs a dedicated connection and reconnect handling. Deferred until a consumer runs readers in a second process.
- Return `(seq, inserted)` from a keyed append: a caller that needs to know can compare against a read; the plain `long` matches `AppendEventAsync`.

## Consequences

- A consumer upgrading the package gets the SQLite schema upgrade on first open (version 1 → 2, additive); an older library then refuses the file rather than guessing.
- A Postgres host must run `0002_event_stream.sql` (it is in `PgStepStoreSchema.Sql`); before that, event reads and appends through `PgStepStore` fail on the missing columns.
- The Postgres `seq`-before-commit gap (a reader can miss a lower `seq` committed late) stays; the stream is for observers that tolerate it or poll from a lower mark.
