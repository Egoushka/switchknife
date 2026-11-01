# Switchknife.Journal.Bench

Measures what a hash-chained, append-only event log costs on top of [`Switchknife.Journal`](../../src/Switchknife.Journal) and [`Switchknife.Journal.Postgres`](../../src/Switchknife.Journal.Postgres), for the rooms log proposed in ADR 0007 (open pull request). Not a package. Results and verdict: [`docs/benchmarks/journal-p95.md`](../../docs/benchmarks/journal-p95.md).

- `Appenders.cs` has four stores behind one `IAppender`: an in-memory chain, a Postgres chain (the append the ADR describes, modelled as plain SQL on top of the library's own `0001_steps.sql`; the real `PgRoomLog` does not exist yet), and the two shipped stores unchained (`PgStepStore`, `SqliteStepStore`) for comparison.
- `Chain.cs` is the hash: `body_hash` = SHA-256(body), `hash` = SHA-256 over a length-prefixed envelope ending in `body_hash` and `prev_hash`. `HashRow` in `Runner.cs` times each form alone.
- `Bodies.cs` makes JSON tool-call bodies of 1 KB, 10 KB and 100 KB; `--incompressible` swaps the log-like text for random bytes.
- `DelayRelay.cs` is a TCP relay that delays each direction, so `--rtt-ms 25,50` shows a remote database without a network.
- `Stats.cs` uses nearest-rank percentiles. The order of the loops (size, then scenario, then store) spreads server stalls over every store.

```bash
# the database name must contain "bench"; the run drops and recreates steps and step_events
dotnet run -c Release --project bench/Switchknife.Journal.Bench -- run \
  --connection "Host=localhost;Database=journal_bench;Username=postgres;Password=..." \
  --rtt-ms 0,25,50 > run.md
```

Options: `--scale` (multiplies the run counts), `--writers` (8), `--rtt-ms` (comma list; 0 skips the relay table), `--incompressible`, `--no-sqlite`. The connection can come from `JOURNAL_BENCH_PG` instead. Needs a PostgreSQL server; any recent official image works.
