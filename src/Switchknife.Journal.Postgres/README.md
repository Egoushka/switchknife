# Switchknife.Journal.Postgres

A [Switchknife.Journal](../Switchknife.Journal) `IStepStore` on PostgreSQL, for a flow shared by more than one process: `PgStepStore` leases a row (`lease_until`) instead of the in-process semaphore `SqliteStepStore` uses, so two processes claiming the same key at once agree on which of them runs the step.

```csharp
await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();
await using (var ddl = new NpgsqlCommand(PgStepStoreSchema.Sql, connection))
    await ddl.ExecuteNonQueryAsync();            // or run Migrations/0001_steps.sql with your own migrator

await using var dataSource = NpgsqlDataSource.Create(connectionString);
await using var journal = StepJournal.Open(new PgStepStore(dataSource));

var container = await journal.RunAsync("batch-7", "prep/task-3", async (step, ct) =>
{
    var name = $"prep-{step.Flow}-task-3";
    if (step.Resumed)
        await engine.RemoveIfExistsAsync(name, ct);
    return await engine.CreateAndStartAsync(name, ct);
}, MyJson.Default.ContainerInfo);
```

`StepJournal`'s guarantees (completed key never reruns, unfinished key resumes with `Resumed = true`, a failed step records its error and the next run is a fresh attempt) are unchanged: `PgStepStore` is a drop-in `IStepStore`, so existing code written against `StepJournal.OpenAsync` (SQLite) ports to Postgres by constructing it with `StepJournal.Open(new PgStepStore(dataSource))` instead.

## Schema

`PgStepStoreSchema.Sql` (embedded from `Migrations/0001_steps.sql`) creates `steps` (one row per `flow, key`, its `state`, `attempt`, `result jsonb`, `lease_until`) and `step_events`. Run it with your own migrator, or with [Switchknife.Migrations](../Switchknife.Migrations) by embedding the same script in your host's assembly. `PgStepStore` does not create or alter these tables itself.

## Multi-process claims

`ClaimAsync` locks the row (`select ... for update`) in its own short transaction, decides from its state and `lease_until` whether to hand the caller a claim, and commits before the step itself runs — so the row is not held locked for the step's duration. A lease still active when another process asks belongs to whoever is currently running the step: `ClaimAsync` waits it out and retries rather than stealing it. A lease left behind by a crashed process (no `CompleteAsync`/`FailAsync` ever ran) simply expires, and the next claim resumes it (`Resumed = true`).

`StepOptions` controls this:

```csharp
new PgStepStore(dataSource, options: new StepOptions
{
    Lease = TimeSpan.FromSeconds(30),     // longer than the slowest step
    Retries = 50,                          // how many times to wait out another process's lease
    Backoff = TimeSpan.FromMilliseconds(200),
});
```

Giving up (all retries exhausted while another process holds the lease) throws `TimeoutException`.

## Exactly-once inside your own transaction

A step that only touches the database can write its result in the caller's own transaction instead of taking a lease at all — rollback then erases the claim along with everything else the step did:

```csharp
await using var transaction = await connection.BeginTransactionAsync();
var row = await store.RunInTransactionAsync(transaction, "batch-7", "insert/customer-9", async (step, tx, ct) =>
{
    await using var insert = new NpgsqlCommand("insert into customers (...) values (...)", connection, tx);
    await insert.ExecuteNonQueryAsync(ct);
    return new CustomerRow(9);
}, MyJson.Default.CustomerRow);
await transaction.CommitAsync();
```

A key already completed returns its recorded result and does not run `step` again; otherwise `step` runs and its result is written in the same transaction, so it is visible exactly when the caller's own changes are.

## Limits

- PostgreSQL only (`NpgsqlDataSource`); the store does not own or dispose it.
- Results are JSON through a `JsonTypeInfo<T>` you pass, same as `SqliteStepStore`, so it works with Native AOT.
- Not a workflow engine: no timers, no scheduling. It records what happened so your own code can decide what to do next.
