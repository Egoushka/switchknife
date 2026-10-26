# Switchknife.Journal

A durable step journal on SQLite for processes that must survive a restart in the middle of their work.

A step is one side effect with a stable key inside a flow (`batch-7`, `prep/task-3`). `RunAsync` records the step's intent before it runs and its result after:

- a completed key never runs again; its recorded result comes back;
- a key whose earlier attempt recorded its intent and never its outcome (a crash, a kill, a cancellation) runs again with `context.Resumed = true`, so the step adopts or removes what that attempt left;
- a step that throws is recorded as failed and rethrown; the next run of the key is a fresh attempt.

Storage is behind `IStepStore`; `StepJournal.OpenAsync(path)` opens the built-in SQLite store (`SqliteStepStore`). A flow shared by more than one process needs a store with real locking between processes — [Switchknife.Journal.Postgres](../Switchknife.Journal.Postgres)'s `PgStepStore` is one — built with `StepJournal.Open(new PgStepStore(dataSource))` instead.

```csharp
await using var journal = await StepJournal.OpenAsync("state/journal.db");

var container = await journal.RunAsync("batch-7", "prep/task-3", async (step, ct) =>
{
    var name = $"prep-{step.Flow}-task-3";          // deterministic: a resumed attempt finds what it made
    if (step.Resumed)
        await engine.RemoveIfExistsAsync(name, ct);   // adopt or remove what the interrupted attempt left
    return await engine.CreateAndStartAsync(name, ct);
}, MyJson.Default.ContainerInfo);

await journal.AppendEventAsync("batch-7", "task_started", """{"task":"task-3"}""");
var replay = await journal.ReadEventsAsync("batch-7", after: lastSeen);
var leftovers = await journal.UnfinishedAsync();     // on start: what a crash left mid-step
```

To run a side effect again under the same key on purpose (a workspace volume removed and prepared again under the same run id), reset the step first; the next run is a first attempt:

```csharp
await journal.ResetAsync("batch-7", "prep/task-3");   // true when there was a record; throws when the step is still started
```

Guarantees and limits:

- A step runs at least once; its result is recorded at most once. A step that finished but crashed before its result was written runs again, resumed, so make each step safe to repeat for its key.
- `ResetAsync` removes a completed or failed step's record, so its key runs as a first attempt (`Attempt` 1, `Resumed` false); it returns false when the key had no record. Reset is your statement that the earlier effect is gone or may repeat: the journal cannot check it. A started step is refused (`InvalidOperationException`): an interrupted attempt must be resumed, not forgotten.
- Every commit is synced to disk (`synchronous = FULL`, WAL).
- `SqliteStepStore`: one process owns a journal file; calls are serialized inside it. Run one step of a given key at a time. A store built for more than one process (`PgStepStore`) instead leases the row; see its own README.
- Results are stored as JSON through a `JsonTypeInfo<T>` you pass (source-generated), so it works with Native AOT.
- Not a workflow engine: no timers, no scheduling, no distribution. It records what happened so your own code can decide what to do next.
