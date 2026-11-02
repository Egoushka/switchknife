# Journal append latency with a hash chain: does the rooms log need Rust?

Why this exists: the rooms spec (ADR 0007) puts a hash-chained, append-only event log on `Switchknife.Journal` and says to move the hot path to Rust only if ingest adds more than 100 ms p95 per tool call. This measures the append, the part Rust would replace, before anyone writes a line of Rust. Harness: `bench/Switchknife.Journal.Bench` (see its README).

## Conclusion

**C# passes the 100 ms p95 line at every body size for a single writer, which is what a tailer is: p95 3.1, 4.8 and 8.0 ms at 1, 10 and 100 KB, and at most 9.0 ms in a second run with random bytes. Recommendation: no Rust log core.** Eight writers into one room is not the design's shape and was unstable here: p95 20 to 45 ms in the first run, but 122 ms at 1 KB in the second (see "Sensitivity"). SHA-256 is not the cost: the whole chain computation is 1.7 µs at p95 and the 100 KB body hash is 0.13 ms at p95, under 2% of the 8.0 ms append. The cost is the database round trips.

One configuration crosses the line, and a language change does not fix it: Postgres at 50 ms of network round trip (p95 119 to 148 ms; at 25 ms it is 62 to 72 ms), because an append is two round trips (see "Remote database"). Rust on SQLite would only help by running next to the data, which `SqliteStepStore` already does in C#.

The redactor question stays open: fewer than two real consumers exist (see "Shared redactor").

## What was measured

| Item | Value |
|---|---|
| Hardware | Apple M1 (MacBookPro17,1), 8 logical CPUs, 16 GB, macOS 27.0.1 |
| Runtime | .NET 10.0.10, Release build |
| Server | PostgreSQL 18.6 in a container (OrbStack VM, 4 CPUs, 4.2 GB) on loopback; `fsync=on`, `synchronous_commit=on` |
| Bodies | JSON `{tool,input,output}` of 1 KB, 10 KB and 100 KB; 64 distinct per size; log-like text (compressible) |
| Runs per size | 1 KB 5000, 10 KB 3000, 100 KB 1000 (the 8-writer scenarios run half); first 10% (at least 50) dropped as warm-up |
| Percentiles | nearest rank; loops run size, then scenario, then store, so a server stall hits every store |

Stores:

- **Postgres, chained**: the append ADR 0007 describes, modelled as SQL on the library's own `0001_steps.sql` plus the `0002_rooms` columns and indexes. Begin, advisory lock on the room, read the previous `room_seq` and `hash` (one round trip); insert the event with `body_hash`, `prev_hash` and `hash`; commit (second round trip). The real `PgRoomLog` does not exist, so this is a model, and its numbers are the best guide until it does.
- **Postgres, unchained**: `PgStepStore.AppendEventAsync` as shipped.
- **SQLite file, unchained**: `SqliteStepStore` as shipped.
- **memory, chained**: the same hashing with no I/O, so the cost of the chain alone shows.

Scenarios: 1 writer; 8 writers in 8 rooms; 8 writers in 1 room (the advisory lock serialises them, the worst case).

## Results

Append latency in ms, Postgres, chained. The first block is the decisive one: a tailer is a single writer.

| scenario | body | n | p50 | p95 | p99 | max | appends/s |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 writer | 1 KB | 5000 | 1.18 | 3.09 | 6.84 | 86.4 | 629 |
| 1 writer | 10 KB | 3000 | 2.26 | 4.78 | 7.54 | 93.5 | 376 |
| 1 writer | 100 KB | 1000 | 4.36 | 8.03 | 14.59 | 43.6 | 200 |
| 8 writers, 8 rooms | 1 KB | 2496 | 2.88 | 9.90 | 24.76 | 29.9 | 2042 |
| 8 writers, 8 rooms | 10 KB | 1496 | 3.78 | 7.74 | 11.84 | 27.1 | 1814 |
| 8 writers, 8 rooms | 100 KB | 496 | 11.14 | 21.32 | 35.19 | 40.6 | 632 |
| 8 writers, 1 room | 1 KB | 2496 | 9.52 | 20.23 | 30.07 | 255.4 | 680 |
| 8 writers, 1 room | 10 KB | 1496 | 16.68 | 23.03 | 27.90 | 40.5 | 459 |
| 8 writers, 1 room | 100 KB | 496 | 34.27 | 44.96 | 65.82 | 66.4 | 223 |

Against the shipped stores, single writer, p95 in ms:

| body | Postgres chained | Postgres unchained | SQLite unchained | memory chained |
|---|---:|---:|---:|---:|
| 1 KB | 3.09 | 2.04 | 0.19 | 0.00 |
| 10 KB | 4.78 | 4.00 | 0.31 | 0.01 |
| 100 KB | 8.03 | 8.00 | 2.49 | 0.17 |

The chain adds about 1 ms at 1 KB (the extra read and the lock) and nothing visible at 100 KB, where the insert dominates. The full table, with p50, p99 and max for every store and scenario, is in `docs/benchmarks/results/2026-11-01-journal-p95.md`.

### Hash cost alone

Microseconds per call, p50 / p95 / p99.

| body | `body_hash` = SHA-256(body) | chain link (envelope + `body_hash` + `prev_hash`) | SHA-256(body ‖ `prev_hash`) | body throughput |
|---|---|---|---|---:|
| 1 KB | 0.7 / 1.2 / 2.1 | 1.5 / 1.7 / 3.7 | 0.9 / 1.1 / 2.0 | 1153 MB/s |
| 10 KB | 5.7 / 10.3 / 20.3 | 1.5 / 1.8 / 3.8 | 5.9 / 6.2 / 15.8 | 1407 MB/s |
| 100 KB | 58.7 / 126.6 / 505.2 | 1.5 / 1.7 / 2.0 | 76.7 / 134.3 / 384.8 | 1229 MB/s |

The ADR's design (hash the body once, chain over `body_hash`) costs the body pass plus 1.5 µs. Hashing body and `prev_hash` together costs about the same body pass, so the ADR's form is no dearer and also lets a body be purged. The 505 µs p99 at 100 KB is a scheduler hiccup on a laptop (2000 samples), not a property of SHA-256.

### Remote database

An append is two round trips, so a remote server adds about two times the round trip to every append. A TCP relay delays each direction by half the round trip; single writer, chained Postgres, ms:

| round trip | body | n | p50 | p95 | p99 | max |
|---:|---|---:|---:|---:|---:|---:|
| 25 ms | 1 KB | 500 | 55.8 | 71.8 | 131.2 | 408.1 |
| 25 ms | 10 KB | 300 | 55.9 | 65.5 | 87.6 | 219.5 |
| 25 ms | 100 KB | 100 | 58.0 | 61.7 | 68.8 | 69.0 |
| 50 ms | 1 KB | 500 | 108.2 | 118.5 | 153.1 | 265.9 |
| 50 ms | 10 KB | 300 | 109.5 | 148.2 | 215.6 | 231.8 |
| 50 ms | 100 KB | 100 | 111.6 | 123.8 | 139.1 | 145.9 |

The tailer sits off the tool path, so a slow append delays the log, not the tool call. The line only binds if appends fall behind the event rate. I did not measure the real round trip from the laptop to the server (the probe hung), so the deployed value is UNKNOWN. If it is 25 ms or more, batch the events of one scan into one transaction (one lock, one read, N inserts, one commit); that costs one pair of round trips per batch instead of per event. Not built, not measured.

## Verdict against the stop line

| Question | Answer | Evidence |
|---|---|---|
| Local Postgres, single writer, any size, p95 under 100 ms? | Yes | 3.09 / 4.78 / 8.03 ms (run 1); 5.42 / 7.54 / 9.02 ms (run 2, random bytes) |
| Eight writers in one room? | Not settled | p95 20.2 / 23.0 / 45.0 ms in run 1; 121.8 / 59.1 / 52.8 ms in run 2, with up to 410 ms max; a queue on the room's advisory lock, noisy under load |
| Does hashing matter? | No | chain link 1.7 µs p95; 100 KB body hash 126.6 µs p95 |
| Does Rust help? | No | the cost is in database round trips and the insert; a free hash would save under 2% of the 100 KB append (126.6 µs of 8.03 ms) |
| Where does the line break? | Remote Postgres somewhere between 25 and 50 ms of round trip | emulated rows above: p50 is about twice the round trip plus 6 to 12 ms at every size; p95 adds 4 to 39 ms more |

Stop condition from the spec: C# passes, so there is no Rust log core. Option E in ADR 0007 stays deferred.

If the rooms log ever has to run on the phone (no .NET there), that is a distribution reason for a second implementation, not a latency one, and this bench does not decide it.

## What this does not cover

- **A modelled append, not `PgRoomLog`.** It issues the same statements the ADR specifies; the real class may add a purge check or a fork lookup. Re-run `run` against it when it exists.
- **The scanner and the secret mask.** The spec's slice 1 check measures the whole tailer (scan, mask, two appends per tool call) against the tool round trip. This bench is only the append, the part Rust would replace. As a rough bound, two appends per call are about 16 ms at 100 KB p95.
- **Loopback through a Docker VM on a Mac.** A Linux host with a local socket would differ, in a direction not measured (UNKNOWN). The numbers are a laptop figure, not a server figure.
- **Compressible bodies.** The default bodies are log-like text. See "Sensitivity" for random bytes.
- **Table growth.** The schema is created once per run, and `step_events` ended at roughly 37,000 rows (the sum of all Postgres scenarios). A table of millions of rows is UNKNOWN.
- **A loaded laptop.** Other sessions kept working during both runs: the load average read 9.8 over the last 15 minutes of run 1 and 15.6 over the last minute of run 2, on 8 cores. Quiet-machine numbers are probably better, by an amount not measured.
- **Single run.** One run per cell. Percentiles at n of 500 or fewer (p99 especially) are noisy; the 255 ms maximum at 1 KB with 8 writers in one room is one stall in 2496.

## Sensitivity: random bytes

A second run with `--incompressible --scale 0.4 --no-sqlite --rtt-ms 0` (incompressible bodies, so TOAST cannot shrink them; 40% of the run counts). Chained Postgres, ms:

| scenario | body | n | p50 | p95 | p99 | max |
|---|---|---:|---:|---:|---:|---:|
| 1 writer | 1 KB | 2000 | 1.67 | 5.42 | 13.15 | 59.4 |
| 1 writer | 10 KB | 1200 | 2.74 | 7.54 | 24.74 | 118.8 |
| 1 writer | 100 KB | 400 | 4.50 | 9.02 | 22.94 | 40.1 |
| 8 writers, 8 rooms | 1 KB | 1000 | 3.34 | 10.33 | 15.71 | 16.6 |
| 8 writers, 8 rooms | 10 KB | 600 | 4.74 | 21.59 | 31.42 | 31.7 |
| 8 writers, 8 rooms | 100 KB | 200 | 12.21 | 28.82 | 34.84 | 35.8 |
| 8 writers, 1 room | 1 KB | 1000 | 25.42 | 121.79 | 232.64 | 410.5 |
| 8 writers, 1 room | 10 KB | 600 | 25.87 | 59.11 | 218.75 | 254.1 |
| 8 writers, 1 room | 100 KB | 200 | 32.11 | 52.81 | 61.71 | 62.0 |

Single-writer p95 moved by at most 2.8 ms against run 1, so compressibility is not the factor. The 8-writers-in-1-room row at 1 KB is: p50 25 ms against 9.5 ms in run 1, with the unchained store at 3.1 ms p50 in the same run. Eight appends queue on the room's advisory lock, each holding it across two round trips and a commit with `fsync`, so one slow flush delays all eight. I did not isolate the cause (load or disk flush); a quiet re-run would. It matters only if several writers feed one room at once. The spec's slice 1 has one tailer per room; if several writers ever feed one room, batch their events (one lock per batch) and re-measure first.

## Shared redactor

Question: does one redactor, built once, serve the approvals shell (Android, Kotlin), the rooms log (C#, server side) and tin (Rust core)?

### What a shared redactor needs

1. **A rule set with checks.** Spans in, spans out, `{type, start, end, score}` with UTF-16 offsets (the PasteGuard contract that oberih keeps), and a shared fixture so two ports are tested against the same cases.
2. **Deterministic recognisers.** Pattern plus checksum: UA tax id (РНОКПП), RU INN, SNILS, and two passport formats gated by a nearby keyword. Email, IBAN and card are deterministic upstream. Phones need a regional parser.
3. **A merge step.** Ids outrank other spans on overlap; adjacent address fragments join into one.
4. **A fail-closed rule.** If the detector is unavailable, refuse; never answer "no PII".
5. **Optionally a model.** Person and location detection is the weak part of rules (oberih's eval puts the full pipeline at F1 0.890 against 0.773 for the upstream default). That part is a GLiNER or spaCy model.

### oberih's rule set and where it ports

The rules are about 100 lines in `app.py`: five recognisers (`UA_TAX_ID`, `RU_INN`, `RU_SNILS`, `UA_PASSPORT`, `RU_PASSPORT`), a keyword window for the passports, and the merge. The pieces:

| Piece | Kotlin | Rust |
|---|---|---|
| Digit-run regex with lookbehind and lookahead (`(?<![\w+\-])(?<!\d[.,])`) | Native in `java.util.regex`; set Unicode flags (`UNICODE_CHARACTER_CLASS`, `UNICODE_CASE`), because Java's `\w` and case-folding are ASCII by default and Python's are not | The `regex` crate has no lookaround. Use `fancy-regex` or replace each lookaround with a one-character check around the match (all of oberih's are single-character tests) |
| Checksums (INN, РНОКПП, SNILS) | A few lines each; SNILS is already five lines in `app.py`; INN and РНОКПП come from `python-stdnum` and need porting with tests | Same |
| Offsets | Native UTF-16, which the contract uses | UTF-8 strings; needs the conversion oberih already does in Python |
| Phones by region | libphonenumber's Java library | UNKNOWN (not checked) |
| Person and location model | Not portable as rules; the model is 2.09 GiB RAM and 275.9 ms mean per request (oberih eval) | Same |

So the deterministic part ports to Kotlin with little friction and to Rust with a lookaround workaround. The model part does not go on a device; it stays a service call.

### Who would run it on a device

| Consumer | Runs where | Needs a redactor? | Evidence |
|---|---|---|---|
| Approvals shell | Phone (Kotlin) | Yes, and has one | `Sharing.kt` has a heuristic `Redact`, the server has a TypeScript guard in `provider.ts`, and `contract/fixtures/gate/redaction.json` holds 20 or more planted cases both run against |
| Rooms log | Laptop and server (C#) | Yes, before `body_hash`, but a secret mask and the sharing-mode filter, not Cyrillic PII | ADR 0007 decision 9; not built. It can call the oberih HTTP service, so it needs no port |
| tin | Rust core; its policy names Android packages | No evidence | `core/src/policy.rs` is an allow, approval and deny gate with a money rule; a search of its core and docs found no log and no redaction step |

The only consumer that needs a redactor on the device is the shell, and it already has one in Kotlin. Rooms is server side and can call oberih. tin has no stated need.

### Consumer count

**Fewer than two real consumers.** One exists and works (the shell). One is specified but unbuilt and does not need an on-device port (rooms). One has no evidence of a need (tin). By the rule to abstract from two working consumers, the shared redactor is not started.

What would change the answer, in order of cost:

1. Rooms ships its secret mask and sharing filter, and the shell's `Redact` and rooms' filter turn out to share rules. Then a shared fixture is the first step, not a library.
2. tin gains a log or a share path that carries user text.

The cheapest next step costs no code: move oberih's 159 checked id cases (`gen_ids.py --check`) beside the shell's 20-case fixture in one format, and run the shell's Kotlin `Redact` against the id cases. The score tells whether the two rule sets overlap before anyone writes a port. Stop: if the shell's `Redact` already catches the ids that oberih's eval plants, there is nothing to share on device.

## Reproduce

```bash
dotnet run -c Release --project bench/Switchknife.Journal.Bench -- run \
  --connection "Host=localhost;Database=journal_bench;Username=postgres;Password=..." \
  --rtt-ms 0,25,50 > run.md
```

The database name must contain `bench`; the run drops and recreates `steps` and `step_events`. Method and options: `bench/Switchknife.Journal.Bench/README.md`.
