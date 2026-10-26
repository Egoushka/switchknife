# Facts library: spec

Design and reasons: `docs/adr/0001-facts-library.md`. Numbers to collect: `docs/benchmarks/facts-library.md`. Status: proposed, waiting on Yehor's approval of this spec and on gate G0.

## Goal

Two projects, Nytka and feed-eater, record what is true about people and things as facts that carry two timelines (when true in the world, when this database learned it) and a pointer to where they were read. A new fact ends an old one without erasing it. A caller can ask what was true at a date, what was believed at a date, where a fact came from, and can invalidate one. Both projects use one package, not two copies, and the home server's memory budget holds.

## Out of scope

- Changing Hindsight, or exporting to it.
- A graph database, entity summaries, communities, multi-hop traversal.
- A model service or embedder of its own; consumers pass embeddings in.
- An MCP server process. Tool definitions only.
- Moving Nytka's or feed-eater's user interface. Consumer slices change their readers, not their screens, until N2.
- Real conversation or article text in this repository.

## Work-in-progress limit

At most three open implementation pull requests in this repository (`context/cycle.md`). Slices below are ordered; start the next only when fewer than three are open. Consumer slices count against their own repository's limit. Review the spec, not the diff: a pull request merges when its checks below pass and CI is green; Yehor reads diffs for the schema script and for purge (data loss).

## Checks that run in this pull request

These run today. H1 and H2 need Docker; without it, outside CI, they skip (as the other Postgres tests here do) and CI runs them.

| ID | Check | Command | Passes when |
|---|---|---|---|
| H1 | Schema applies and refuses overlap | `dotnet test --project tests/Switchknife.Facts.Bench.Tests --filter-method "*cannot_overlap*"` | the test is green: the schema applies to an empty database on pgvector/PostgreSQL 18, and a second live value for one slot at overlapping times fails with SQLSTATE `23P01` |
| H2 | Design write path agrees with the oracle | `dotnet test --project tests/Switchknife.Facts.Bench.Tests --filter-method "*every_as_of_query*"` | `AsOfWrong` is 0, `AsOfOverlap` is 0, retired rows exist, vector query returns rows |
| H3 | Scorer and percentiles | `dotnet test --project tests/Switchknife.Facts.Bench.Tests --filter-class "*HarnessTests"` | green, no Docker needed |
| H4 | Harness refuses a database not made for it | `dotnet run --project bench/Switchknife.Facts.Bench -- run --connection "Host=x;Database=app"` | exit code 2 and the message names the database |
| H5 | Repository rules | `dotnet build -c Release -warnaserror && dotnet format --verify-no-changes` | exit 0 |

## Acceptance checks for the library slices

Each is a test the slice adds, named here so the name is the contract. "Fake extractor" is the scripted `IFactExtractor` in `Switchknife.Facts.Testing`. "Timeline" is the deterministic history and oracle from `bench/`.

| ID | Behaviour | Test (xUnit, `tests/Switchknife.Facts.Tests`) | Passes when |
|---|---|---|---|
| A1 | As-of valid time | `As_of_valid_time_returns_the_value_the_oracle_expects` | for 2,000 random `(subject, predicate, T, K)` on the Timeline, 0 disagreements |
| A2 | As-of transaction time | `As_of_known_time_does_not_see_later_learning` | a fact recorded after K never appears; one retired after K still appears |
| A3 | Supersession keeps history | `A_new_value_retires_the_old_row_and_keeps_both` | after superseding, 2 rows for the slot plus 1 narrowed copy; `Explain(old)` names the successor |
| A4 | Late fact | `A_fact_learned_late_ends_where_the_next_known_value_begins` | inserting an earlier version after a later one leaves one live value at every instant |
| A5 | Slot invariant | `Two_live_values_for_one_slot_cannot_overlap_even_by_raw_sql` | raw insert fails with `23P01` |
| A6 | Cardinality | `Many_and_event_predicates_are_never_superseded_by_rule` | adding a second `likes` leaves both live |
| A7 | Idempotent episode | `Ingesting_the_same_episode_twice_changes_nothing` | row counts equal, fake extractor called once |
| A8 | Changed source | `A_changed_source_retires_facts_it_no_longer_supports` | facts only the old hash supports are `source_changed`; re-asserted ones stay |
| A9 | Atomic failure | `A_failing_extractor_leaves_no_partial_facts` | episode `failed`, 0 facts, retry succeeds |
| A10 | Retraction sticks | `A_retracted_fact_is_not_extracted_again` | re-ingesting text that yields the same fingerprint writes 0 rows |
| A11 | Replaces is checked | `A_replaces_id_that_is_not_a_live_fact_of_the_subject_is_dropped` | fact stored, nothing retired |
| A12 | Trust | `A_third_party_episode_cannot_retire_an_owner_fact` | owner fact stays live; the new fact is stored |
| A13 | Purge and rebuild | `Purging_a_source_rebuilds_the_slot_as_if_it_never_existed` | state equals a store that never saw that episode; and replaying an untouched slot reproduces it exactly |
| A14 | Provenance | `Every_extracted_fact_explains_its_sources` | `Explain` returns at least one source with locator for every fact on the Timeline; a fact without one cannot be inserted |
| A15 | Entity resolution | `Cross_script_spellings_resolve_to_one_entity_and_distinct_names_stay_apart` | the five variant groups and nine distinct names from chronicle's `test_resolve.py` (copied as synthetic data) pass |
| A16 | External identity | `An_external_ref_never_gets_a_second_entity` | 100 mentions of one `external_ref` leave 1 entity |
| A17 | Merge is reversible for earlier facts | `Unmerge_restores_aliases_and_earlier_facts` | state before merge equals state after merge then unmerge, for facts recorded before the merge |
| A18 | Namespace isolation | `One_namespace_never_sees_anothers_facts` | queries in `nytka` return 0 rows of `feed-eater` in a shared database |
| A19 | Caller's transaction | `Ingest_inside_a_caller_transaction_rolls_back_with_it` | caller rolls back: 0 rows, observer saw nothing committed |
| A20 | Tool definitions | `Each_tool_definition_validates_input_and_read_tools_cannot_write` | invalid input rejected; read tools open a read-only transaction |
| A21 | AOT | `scripts/aot-smoke.sh` | the smoke sample ingests with the fake extractor and answers an as-of query without the JIT |
| A22 | Migrations | `The_schema_applies_through_Switchknife_Migrations_beside_a_consumer_schema` | a second assembly's scripts and the library's both run once; second run applies 0 |

## Acceptance checks for the benchmark gates

These use `bench/` (mechanics, synthetic) and the private labelled set (extraction). Thresholds marked "target" are guesses to be replaced by the first measured baseline; a target changes only by a new ADR line, never silently.

| ID | Gate | Command | Passes when |
|---|---|---|---|
| G0 | Beats doing nothing | `dotnet run --project bench/Switchknife.Facts.Bench -- score --gold gold.jsonl --pred predicted-A.jsonl` and the same for B0, then the as-of question run in the benchmark doc | A's as-of accuracy is at least 10 points above B0's, with at least 25 superseded pairs; both reported with intervals |
| G1 | Ingest latency, database only | `run --facts 50000 --subjects 2000` | `ingestPerEpisode.p95` at most 30 ms (target, episode of 5 facts) |
| G2 | As-of query latency | same run | `asOf.p95` at most 10 ms (target) with 0 wrong answers |
| G3 | Hybrid search | same run, `--vector-dims 1024` | `nearest.p95` at most 50 ms (target) |
| G4 | Footprint | same run | `harnessPeakRssMb` at most 64 over an idle harness; `factsBytes` at most 150 MB without vectors; `embeddingBytes` at most 100 MB at 1024 dimensions |
| G5 | Throughput | same run | `factsPerSecond` reported; no threshold, used to size backfill |
| G6 | Extraction | `score` on 60 episodes | precision and recall reported with Wilson intervals; target F1 at least 0.70 on typed predicates (UNKNOWN baseline) |
| G7 | Cost | gateway logs for the run | tokens in and out per episode and cost per 100 episodes reported |

## Slices

One pull request each, in this order. "Issue" means a chargehand issue carrying its own checks, as the cycle requires.

| Slice | Content | Checks | Depends on |
|---|---|---|---|
| F0 | This pull request: ADR, spec, benchmark plan, harness | H1 to H5 | none |
| G0 | Run the baseline: B0, B1 and the design on the labelled set; report | G0 to G7 | F0 merged, labelled set drawn by Yehor |
| F1 | Package skeleton, embedded schema, `Switchknife.Migrations` integration, AOT smoke case, README | A5, A21, A22 | G0 go |
| F2 | Write path: episodes, cardinality, supersession, retraction, trust, purge | A1 to A4, A6 to A13 | F1 |
| F3 | Retrieval: as-of, history, explain, search | A1, A2, A14, G2, G3 | F1 |
| F4 | Entities: resolution, aliases, merge | A15 to A17 | F1 |
| F5 | Extraction contract, prompt kit, `Switchknife.Facts.Testing` | A7, A9, A11 | F2 |
| F6 | Tool definitions | A20 | F3 |
| N1 | Nytka shadow writes (Nytka repository) | counts equal on the labelled set | F5 |
| E1 | feed-eater episodes and entity timeline (feed-eater repository) | its own spec | F5, F4 |
| N2, N3 | Nytka readers, then views over `facts` | Nytka's spec | N1 |

With the limit of three: after G0, start F1; when it merges start F2, F3 and F4 together; F5 and F6 follow as slots free.

## Versions

Tag prefix `facts/v` (MinVer, one `MinVerTagPrefix` in the project). Each slice is a patch; the minor `0.2.0` marks "both consumers read from it" (N2 and E1). `0.x` stays until two projects use it in production, per the README.

## Open for Yehor

1. Approve this spec, or send it back.
2. Draw the labelled set (30 Nytka conversations, 30 feed-eater items, at least 25 planted or real supersessions) into a private folder; `docs/benchmarks/facts-library.md` says how and what the file looks like.
3. After G0, say go or no-go on F1.
