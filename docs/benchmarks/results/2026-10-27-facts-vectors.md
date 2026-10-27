# Facts library: vector gates G3 and G4, ten runs

Date: 2026-10-27. Why: `docs/specs/2026-10-27-facts-vectors.md`, `docs/adr/0003-facts-vector-storage.md`. The first four runs after the change ranged from 21.5 to 50.8 ms while the laptop was loaded (load average 15 to 38); this repeats the run with the load checked first.

## Method

`dotnet run -c Release --project bench/Switchknife.Facts.Bench -- run --subjects 2000 --facts 50000 --queries 2000 --episode-size 5 --vector-dims 1024 --vector-noise 1.0 --fresh --gate`, ten times in a row on commit 1873954, each run replacing the schema. PostgreSQL 18.x from `pgvector/pgvector:0.8.7-pg18-trixie` (the extension is not used), container limited to 384 MB, Apple silicon laptop with 8 cores, the harness and the container on the same machine. Before each run the script waited until the one-minute load average was below the core count (8); the loads below are the one-minute averages read just before and just after each run. Other work on the machine could not be stopped, so load was low but not zero: the load after run 3 was 49, so run 3, the slowest (35.2 ms), may have overlapped the start of that spike. 52,000 live facts, 200 nearest queries per run, oversample 40. Raw reports: `2026-10-27-facts-vectors/run-NN.json`.

## Result

G3 holds: **nearest p95 was under the 50 ms limit in 10 of 10 runs.** The p95 of each run: median 22.9 ms, min 15.9, max 35.2 (mean 23.6). Median p50 17.4 ms. The earlier 50.8 ms run was load, not the design: its sibling runs at the same settings ranged 21.5 to 37.7. The worst p99 (50.99 ms, run 1) is above the limit, but the gate is on p95.

| Run | Load before | Load after | nearest p50 ms | nearest p95 ms | nearest p99 ms | tie-aware recall | strict recall | embeddings MB |
|---|---|---|---|---|---|---|---|---|
| 1 | 5.33 | 7.55 | 13.6 | 24.2 | 51.0 | 1.000 | 0.89 | 73.2 |
| 2 | 7.55 | 4.71 | 17.6 | 22.9 | 33.3 | 1.000 | 0.90 | 73.2 |
| 3 | 4.71 | 49.17 | 19.3 | 35.2 | 50.3 | 1.000 | 0.89 | 73.2 |
| 4 | 7.18 | 3.69 | 17.1 | 20.0 | 30.0 | 1.000 | 0.90 | 73.2 |
| 5 | 3.69 | 3.74 | 17.0 | 20.5 | 27.5 | 1.000 | 0.89 | 73.2 |
| 6 | 3.74 | 3.51 | 17.9 | 22.9 | 37.6 | 1.000 | 0.91 | 73.2 |
| 7 | 3.51 | 3.31 | 17.6 | 25.6 | 48.9 | 1.000 | 0.90 | 73.2 |
| 8 | 3.31 | 7.47 | 17.5 | 20.8 | 34.7 | 1.000 | 0.90 | 73.2 |
| 9 | 7.47 | 7.50 | 17.4 | 27.5 | 39.8 | 1.000 | 0.90 | 73.2 |
| 10 | 7.50 | 5.82 | 12.5 | 15.9 | 34.5 | 1.000 | 0.89 | 73.2 |

G4 holds in all ten (73.2 MB against 100). Ingest p95 was 10.1 to 14.2 ms (limit 30) and as-of p95 0.27 to 0.52 ms (limit 10) in the same runs.

## What the margin is

The tightest run (35.2 ms) leaves 29% headroom on this hardware. The cost per query is dominated by the linear scan of the codes (about 10 to 15 ms of the 17 ms median, from the `EXPLAIN` in the ADR); it grows with the row count (about 0.25 ms per 1,000 rows), so the margin shrinks as a store grows. Not measured here: the production server, real embeddings, concurrent writers during a search.
