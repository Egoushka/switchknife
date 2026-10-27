# Facts library, F3: Explain and search fusion

Part of `docs/specs/2026-10-26-facts-library.md` (slice F3: retrieval). Design and reasons: `docs/adr/0001-facts-library.md`, sections "Retrieval" and "Tool definitions"; vector search is `docs/adr/0003-facts-vector-storage.md`. Status: this file is committed before the code; each pull request below ticks its own checks. It takes over the W3 section of `2026-10-28-facts-write-path.md` (fusion decisions and checks L2, L2b, F3a to F3d), which now points here.

## Two pull requests

| PR | Contents | Files it owns |
|---|---|---|
| **E** Explain | `ExplainAsync(factId)`: why a fact is believed, from where, and what became of it | `FactsStore.Explain.cs` and `Explanation.cs` (new), tests, the Facts README |
| **S** fusion | `SearchAsync(text, vector, model, …)` by reciprocal rank, and its gate in the harness | `FactsStore.Search.cs` (new), `bench/` (a `fusion` latency and its gate), tests |

They share no file. Neither edits the schema or `FactsStore.Embeddings.cs`: fusion calls the public `NearestAsync`. No schema change means no migration, and the hot write path is untouched.

## E: decisions

`ExplainAsync(Guid factId, CancellationToken)` returns a `FactExplanation`, or null when the id is not a row of this namespace (never throws for a foreign or purged id). Everything comes from one `NpgsqlBatch`, so one round trip.

| Part | Content | Where it comes from |
|---|---|---|
| `Version` | the row as `HistoryAsync` shows it: fact, validity range, `RecordedAt`, `RetiredAt`, `RetiredReason`, `SupersededBy` | `facts.facts` |
| `Sources` | one `FactSource` per source line: episode id, `SourceKind`, `SourceRef`, `ContentHash`, `LocatorJson`, `Quote`, the episode's `Trust`, `OccurredAt`, `RecordedAt` | `facts.fact_sources` joined to `facts.episodes`, ordered by episode `recorded_at`, then id |
| `Trust` | the highest trust among the sources: the trust the write path compares | computed from `Sources` |
| `Retraction` | null when the assertion was never retracted; otherwise the latest log row: `RetractedAt` and `RestoredAt` (null while it blocks) | `facts.retractions` by the row's fingerprint |
| `Successors` | the chain of rows that replaced this one, nearest first: follow `SupersededBy` until a row has none; each element is a `FactVersion` | recursive query, stops at 32 steps and at a repeated id |
| `Predecessors` | the rows whose `SupersededBy` is this row, oldest first (one hop) | `facts.facts` |

What it does not return: the extractor, model and token counts (`episodes` has the columns, but no write path fills them until the extraction contract, slice F5, so a field would always be null); the text of the source (the library never holds it; an `ISourceResolver` that turns `(SourceKind, SourceRef, LocatorJson)` into a link or snippet is the consumer's, as the ADR says, and arrives with the tool definitions, F6); which fact outranked an `outranked` row (its `Successors` is empty, and `Version.RetiredReason` says why).

Each source line of a `one` fact keeps its `assertion*` columns (what the episode asked for); `FactSource` does not expose them, since they exist for purge replay and a reader has the validity range already. A narrowed copy or a tail has its parent's sources copied by the write path, so it explains itself without following a link.

The batch is one round trip, not one snapshot: its statements run in READ COMMITTED, so a write that lands between two of them can show a source or a successor the version row does not yet reflect. Rows are immutable and sources only grow, so the worst case is an explanation one write ahead of its version; a repeatable-read transaction would cost two more round trips.

**The ADR's "a fact without a source cannot be inserted".** The library never writes a fact row without its source line (both go in one batch), and A14 proves it for the whole benchmark workload. A database constraint would need a deferred trigger on every fact insert, on the path G1 limits to 30 ms; it is not added. Raw SQL can still insert a source-less fact, and `Explain` then returns it with an empty `Sources` list rather than hiding it.

## E: acceptance checks

xUnit in `tests/Switchknife.Facts.Tests`, Postgres in Docker as the other tests there. Every earlier test keeps passing unchanged.

| ID | Test | Passes when |
|---|---|---|
| A14 | `Every_extracted_fact_explains_its_sources` | on the benchmark Timeline (200 subjects, live and retired rows), `ExplainAsync` returns, for every fact row, at least one source whose locator is a JSON object and whose `SourceKind` and `SourceRef` are those of an episode that was ingested |
| E1 | `Explain_names_the_episode_locator_and_quote_of_each_source` | a fact ingested with locator `{"line":4}` and quote `"moved to Kyiv"`: one source with exactly those, the episode's kind, ref and hash, `OccurredAt` as given and `RecordedAt` as the clock said |
| E2 | `Explain_of_a_superseded_fact_names_the_successor_and_the_chain` | Lviv, then Kyiv, then Berlin: `Explain(Lviv)` has reason `superseded`, `Successors` is Kyiv then Berlin in that order; `Explain(Kyiv).Predecessors` is Lviv; `Explain(Berlin).Successors` is empty; the narrowed copy of Lviv has Lviv's sources and no successor |
| E3 | `Explain_lists_every_corroborating_source_and_the_highest_trust` | the same value from an owner episode and a third-party episode: two sources, `Trust` is `Owner`; from third-party episodes only: `ThirdParty`; the order of sources follows the episodes' recorded time |
| E4 | `Explain_reports_the_retraction_state` | after `RetractAsync`, `Retraction.RestoredAt` is null and the reason is `retracted`; after `RestoreAsync` the live row has a `Retraction` with `RestoredAt` set; a fact never retracted has `Retraction` null |
| E5 | `Explain_of_an_outranked_fact_says_so_and_has_no_successor` | the third-party Kyiv of A12: reason `outranked`, `Successors` empty, `Trust` `ThirdParty` |
| E6 | `Explain_of_a_fact_retired_by_a_changed_source_still_shows_the_old_version_as_a_source` | jazz retired `source_changed` (A8): reason `source_changed`, and its source carries the old content hash |
| E7 | `Explain_returns_null_for_an_unknown_foreign_or_purged_fact` | a random id, a fact of another namespace in a shared database (A18's setup) and a fact that only a purged source supported (A13c) each return null; a fact that survived the purge no longer lists the purged episode |
| E8 | `Explain_costs_one_round_trip` | the `ActivityListener` counter of WritePathTests sees exactly one command batch for a fact with three sources and a three-step chain |
| E9 | `Explain_of_a_source_less_fact_inserted_by_raw_sql_returns_an_empty_source_list` | the raw insert of A26 without a source line: `Sources` empty, `Trust` is `ThirdParty` (the lowest, since no source vouches for it), no exception |
| E10 | `Successor_chains_stop_at_a_cycle_and_at_32_steps` | raw updates make a two-row cycle and a 40-row chain: the cycle returns each row once, the chain returns 32 |

## S: decisions

`SearchAsync(string? text, ReadOnlyMemory<float> vector, string? model, int limit = 10, DateTimeOffset? validAt = null, DateTimeOffset? knownAt = null, CancellationToken cancellationToken = default)` returns `IReadOnlyList<SearchHit>`, best first. A `SearchHit` is the fact, its fused `Score`, and why it was found: `TextRank` and `VectorRank` (1-based, null when that list did not contain it) and `Similarity` (cosine, null without a vector list). Reading the ranks answers "why is this returned".

**The two lists.**
- Full text: `plainto_tsquery('simple', text)` against `facts.search` (the generated column and its GIN index), ordered by `ts_rank_cd` descending then id.
- Vector: `NearestAsync(model, vector, limit × 4, validAt, knownAt)`.
- Both are `limit × 4` deep, both obey `validAt` (default now) and `knownAt` (default now; a future value is refused with `ArgumentOutOfRangeException`, as in `AsOfAsync` and `NearestAsync`), and both are confined to the store's namespace and to rows not retired at `knownAt`.
- The two run at the same time on two pooled connections, so latency is the slower list, not the sum. F3d is what holds this: sequential lists would add the text query to the 24 to 31 ms nearest p95 already measured, and the budget says whether that is acceptable.

**Fusion.** `score = Σ 1 / (60 + rank)` over the lists that contain the fact, rank 1-based. Order: score descending, then the better of the two ranks ascending, then fact id ascending, so a result is reproducible. A fact in one list still scores, so a list never excludes. The constant 60 is the published default; the benchmark plan in `docs/benchmarks/facts-library.md` decides whether it stays once real labels exist. The candidate lists are deeper than `limit` so that a fact ranked 12th in both lists can rise into the top 10.

**Inputs.**
- `text` null, empty or blank: no text list. `vector` empty: no vector list. Both absent: `ArgumentException` (nothing to search).
- A non-empty vector needs a `model` (`ArgumentException` otherwise); its validation is `NearestAsync`'s (NaN, zero, wrong length → `ArgumentException`; a length different from the stored vectors' → `PostgresException` 22026).
- `limit` is 1 to 1,000 (`ArgumentOutOfRangeException` outside), so the candidate depth stays bounded.
- Text that `plainto_tsquery` reduces to nothing (only punctuation) yields an empty text list, not an error.

**Not here.** The Ukrainian search configuration (ADR: a per-consumer option, still UNKNOWN), query rewriting, and an entity filter (`AsOf` covers "about X").

## S: acceptance checks

| ID | Test | Passes when |
|---|---|---|
| L2 | `Facts_recorded_after_known_at_never_appear_in_search` | text-only, vector-only and fused searches at a `knownAt` before a fact was recorded never return it; a row retired after `knownAt` still does |
| L2b | `Facts_not_valid_at_the_instant_never_appear_in_search` | the same for `validAt` outside a fact's range |
| F3a | `Fusion_ranks_a_fact_found_by_both_above_a_fact_found_by_one` | a fact that is top 3 in both lists beats one that is first in one list and absent from the other |
| F3b | `Text_only_and_vector_only_searches_work` | a null vector or empty text returns that list alone, in its own order, with the other rank null |
| F3c | `Search_never_returns_retired_or_other_namespace_facts` | retracted, superseded-and-closed and other-namespace facts are absent |
| F3e | `Score_is_the_sum_of_reciprocal_ranks_and_ties_break_by_better_rank_then_id` | scores equal `1/(60+r)` summed, computed by hand from the reported ranks, for a fixture with known list orders; two facts with equal scores order by the better rank, and equal ranks by id |
| F3f | `A_fact_ranked_beyond_limit_in_both_lists_rises_into_the_results` | a fact 12th in both lists with `limit` 10 is returned, above facts that are 1st in one list only (candidate depth is `limit × 4`, not `limit`) |
| F3g | `Search_rejects_bad_input` | no text and no vector, a vector without a model, `limit` 0 and 1,001, and a future `knownAt` each throw the exception named above |
| F3d | `Search_stays_inside_its_latency_budget_at_50000_facts` | the harness reports `fusion.p95` for fusion at most 100 ms (twice the vector gate), measured on 2,000 queries of a word from a live fact plus a fresh vector |
| G3, G4 | the harness gates | `nearest.p95` at most 50 ms and `embeddingBytes` at most 100 MB, as before: fusion adds no table and no write |
| G3f | `HarnessTests` | a report whose `fusion.p95` is over 100 ms yields a failure naming `fusion.p95`; one without a fusion measurement (no vectors) yields none |

Every test of both pull requests is shown to fail: the pull request lists the mutation (a line changed in the code) that turns each red.

## Commands

| What | Command |
|---|---|
| Repository | `dotnet build -c Release -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test -c Release`, `dotnet pack src/Switchknife.Facts -c Release -o <dir>` |
| Target size | `dotnet bench/Switchknife.Facts.Bench/bin/Release/net10.0/Switchknife.Facts.Bench.dll run --connection "<bench database, 384 MB container>" --subjects 2000 --facts 50000 --queries 2000 --episode-size 5 --vector-dims 1024 --fresh --gate`: exit 0 |
| Per statement | the same run against a server with `pg_stat_statements` on, to see the text list's plan (a common word may match thousands of rows and `ts_rank_cd` ranks all of them) |

If the text list alone breaks the 100 ms budget at 50,000 facts, the response is, in order: order the matches by the cheaper `ts_rank` first, a partial index on live rows, then a lower candidate depth. The gate stays; the pull request says which one it took and the numbers.

## Not in this plan

Entity resolution and aliases (F4, held until the G0 labelled set exists), the extraction contract and `Switchknife.Facts.Testing` (F5), tool definitions and `ISourceResolver` (F6), forgetting an entity (its own slice, requested by the Nytka desk), and a scorer for fusion quality on real questions (after G0: the constant 60 and the candidate depth are tuned there, not here).
