# Facts library, first code slice: the typed core

Part of `docs/specs/2026-10-26-facts-library.md` (slice F1, plus the part of F2 and F3 a benchmark needs). Design and reasons: `docs/adr/0001-facts-library.md`. Status: implemented in this pull request.

## Why this slice, and the gate it starts before

The slice table makes F1 wait for gate G0 (Yehor's go on the labelled set). This slice is started ahead of it because every outcome of G0 needs it: if A wins, F2 to F6 build on it; if not, the ADR builds "only the feed-eater side", which still needs the schema, the immutable write path and the as-of read. It ships no release tag and no consumer change, so G0 can still stop everything above it at no cost beyond this code.

## Goal

A `Switchknife.Facts` package whose core works and is measured: the schema as an embedded migration, typed predicates with cardinality, an ingest that keeps every row immutable and supersedes by clipping, and a read of "what was true at T, as known at K". The benchmark harness drives this package instead of its own copy of the SQL, so the numbers measure the code consumers will run.

## Out of scope (next slices)

- `ValidTo` on a `one` predicate, entity-valued objects, retraction, trust, changed-source retirement, purge (F2).
- `Explain`, `Search`, vector writes (F3). `History` exists only as the read the tests need.
- Entity resolution and merge (F4); the extractor contract and `Switchknife.Facts.Testing` (F5); tool definitions (F6).
- A `facts/v` release tag. The README says preview.

## Behaviour

A fact has a subject (an entity), a predicate, a value, `ValidFrom` (required) and for `many` an optional `ValidTo`. An episode names its source by `(source_kind, source_ref, content_hash)`; one ingest writes one episode and its facts in one transaction, the caller's if it passes one. `recorded_at` is the store's clock (`TimeProvider`), never the episode's date. A predicate is defined once with a cardinality: `one` (a new value ends the old one), `many` (values coexist), `event` (a point in time). Writers of one slot are serialized by an advisory lock; the exclusion constraint stays the backstop for raw SQL.

## Acceptance checks

All run in CI (tests need Docker, as the other Postgres tests here do; without it, outside CI, they skip). "Timeline" is `Workload` in `bench/`, which answers every as-of question without a database.

| ID | Test (xUnit, `tests/Switchknife.Facts.Tests`) | Passes when |
|---|---|---|
| A1 | `As_of_valid_time_returns_the_value_the_oracle_expects` | 2,000 random `(subject, predicate, T, K)` on the Timeline, 0 disagreements and 0 answers with two values |
| A2 | `As_of_known_time_does_not_see_later_learning` | a fact recorded after K is absent at K; a row retired after K still answers at K |
| A3 | `A_new_value_retires_the_old_row_and_keeps_both` | superseding leaves 3 rows for the slot (old retired, its narrowed copy, the new one); the old row names the new one in `superseded_by` |
| A4 | `A_fact_learned_late_ends_where_the_next_known_value_begins` | an earlier version arriving after a later one leaves exactly one value at every instant and retires nothing |
| A5 | `Two_live_values_for_one_slot_cannot_overlap_even_by_raw_sql` | raw insert fails with SQLSTATE `23P01` |
| A6 | `Many_and_event_predicates_are_never_superseded_by_rule` | a second `likes` and a second `met` leave both live |
| A7 | `Ingesting_the_same_episode_twice_changes_nothing` | the second call reports a duplicate and the row counts of all tables are equal |
| A16 | `An_external_ref_never_gets_a_second_entity` | 100 calls with one `external_ref` leave 1 entity |
| A19 | `Ingest_inside_a_caller_transaction_rolls_back_with_it` | after the caller rolls back, 0 episodes and 0 facts |
| A22 | `The_schema_applies_through_Switchknife_Migrations_beside_a_consumer_schema` | a consumer assembly's scripts and the library's both run once; a second run applies 0 |
| L6 | `Omitting_known_at_means_now_and_a_future_known_at_is_refused` | no default other than the clock; a future value throws |
| L7 | `Recorded_at_comes_from_the_ingest_clock_not_the_episode_time` | an `occurred_at` years before ingest leaves `recorded_at` at the clock |
| C1 | `Concurrent_writers_on_one_slot_leave_no_overlap_and_the_oracle_agrees` | 8 writers racing on one slot: no error, no overlapping live rows, final answers equal a sequential run |
| A21 | `scripts/aot-smoke.sh` | the smoke sample migrates, ingests a superseding pair and answers an as-of question without the JIT |

## Benchmark gates

Thresholds are the spec's targets G1, G2 and G4 (ADR budget), taken as they are; they change only by an ADR line.

| ID | Command | Passes when |
|---|---|---|
| G1/G2/G4, small | `dotnet test --project tests/Switchknife.Facts.Bench.Tests --filter-method "*gates_hold_at_5000_facts*"` | on 5,000 facts, latency in the best of three runs (a shared machine is noisy; a wrong answer fails any run): `ingestPerEpisode.p95` at most 30 ms, `asOf.p95` at most 10 ms, `asOfWrong` and `asOfOverlap` 0, `factsBytes` at most 150 MB |
| G1/G2/G4, target size | `dotnet run -c Release --project bench/Switchknife.Facts.Bench -- run --connection "Host=localhost;Database=facts_bench;..." --subjects 2000 --facts 50000 --queries 2000 --episode-size 5 --fresh --gate` | the same three thresholds at 50,000 facts; exit 3 when any is exceeded (the failing field is named on stderr), 1 when an answer is wrong |
| Gate can fail | `dotnet test --project tests/Switchknife.Facts.Bench.Tests --filter-class "*HarnessTests"` | `Gates_report_every_threshold_a_run_exceeds` is green: a report over each limit yields one failure per limit, a report under them yields none |

Numbers from the target-size run go in the pull request. If one exceeds its threshold the ADR's ordered response applies and this slice reports it instead of moving the threshold.

## Other checks

`dotnet build -c Release -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test`, `scripts/aot-smoke.sh`, `dotnet pack` (the repository's CI). H1 to H4 of the first spec keep passing; H1 now lives in the library tests as A5.
