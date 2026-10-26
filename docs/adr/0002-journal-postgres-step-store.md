# 2. A Postgres step store for Switchknife.Journal

Status: accepted

## Context

`Switchknife.Journal` memoises steps (record intent, store result, a completed key never reruns, an unfinished one resumes with `StepContext.Resumed`) on one SQLite file. Two consumer services run their own PostgreSQL 18 databases and want the same guarantees shared across their own processes, which SQLite's single-writer file cannot give them without the services building locking of their own.

## Decision

- Extract storage behind `IStepStore` (`src/Switchknife.Journal/IStepStore.cs`). `StepJournal` becomes a thin facade over whichever store it is given; its public SQLite-backed surface (`StepJournal.OpenAsync(path, ...)`) keeps its existing signature and behaviour unchanged. `SqliteStepStore` is the extracted, behaviourally unchanged SQLite implementation.
- Ship the Postgres store as its own package, `Switchknife.Journal.Postgres`, rather than inside `Switchknife.Journal`: `Switchknife.Journal` stays free of an `Npgsql` dependency for consumers who only ever use SQLite, matching this repo's one-job-per-package rule (`README.md`). `PgStepStore` references `Switchknife.Journal` for the shared types (`IStepStore`, `StepRecord`, `StepContext`, `JournalEvent`), not the other way around.
- Multi-process coordination is a lease (`steps.lease_until`), not `SELECT ... FOR UPDATE SKIP LOCKED`. `SKIP LOCKED` suits a worker pool dequeuing *any* row from a queue; here, every caller already knows the one `(flow, key)` row it wants, and contention is about how long a claim lasts, not about picking an available row. `ClaimAsync` opens a short transaction, locks that one row with `FOR UPDATE`, decides from its state and lease whether to hand out a claim, and commits before the step itself runs — the row is not held locked for the step's duration. A lease still active belongs to whoever is running the step; a claimant waits it out (`StepOptions.Retries`/`Backoff`) rather than stealing it. A lease left by a crashed process simply expires and the next claim resumes it.
- `RunInTransactionAsync(NpgsqlTransaction, ...)` is a second, simpler path for a step that only touches the database: it writes the result inside the caller's own transaction and needs no lease at all, since the caller's commit/rollback boundary already makes the whole thing atomic — a rollback erases the claim along with everything else the step did.
- The migration (`Migrations/0001_steps.sql`, embedded in `Switchknife.Journal.Postgres`) is exposed as plain SQL (`PgStepStoreSchema.Sql`) for the host to run however it already migrates its own schema (directly, or through `Switchknife.Migrations`), rather than the package running it itself: the host owns its database's migration order.

## Consequences

- A consumer moving a flow from SQLite to Postgres changes one constructor call (`StepJournal.OpenAsync(path)` → `StepJournal.Open(new PgStepStore(dataSource))`); `RunAsync` and friends are unchanged.
- `PgStepStore.ClaimAsync` makes one extra round trip (lock, decide, commit) before every step, compared to `SqliteStepStore`'s in-process semaphore; this is the cost of correctness across processes and is expected to be negligible next to the step itself.
- A lease-based protocol means a step whose actual work outlives `StepOptions.Lease` can be claimed again by another process while the first is still running (duplicate work, not duplicate *recording*, since the loser's claim is only granted after the winner's row is next read as released). Hosts must set `Lease` comfortably longer than the slowest step.
