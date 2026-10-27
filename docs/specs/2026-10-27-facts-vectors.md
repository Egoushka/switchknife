# Facts library, vector slice: closing gates G3 and G4

Part of `docs/specs/2026-10-26-facts-library.md` (retrieval, the vector half of F3). Decision and reasons: `docs/adr/0003-facts-vector-storage.md`. Status: implemented in this pull request.

## The gates that missed

Measured by the harness on the first slice's merge (PostgreSQL 18 with pgvector 0.8.7, container limited to 384 MB, 52,000 live facts, 1024 dimensions, exact scan of `halfvec`, raw SQL probe):

| Gate | Threshold | Before |
|---|---|---|
| G3 nearest, p95 | 50 ms | 261 ms (p50 178) |
| G4 embeddings, bytes | 100 MB | 151.7 MB |

Why, from `EXPLAIN` and relation sizes on that data: the scan computes 52,000 half-precision distances over 1024 dimensions in about 180 ms even with the data in memory (no TOAST, no join), so it is compute- and memory-bound, not a planning problem. A 1024-dimension `halfvec` is 2,052 bytes, over the 2 KB TOAST threshold: the table is 3.5 MB and its TOAST relation 138 MB. Forcing `STORAGE PLAIN` still gives 139 MB (three rows per 8 KB page). The floor for 52,000 rows of 2,052 bytes is 107 MB before any page overhead, so **no layout of 1024-dimension `halfvec` meets 100 MB**; the number in the ADR was arithmetic for 50,000 rows and ignored overhead.

## Approach

Keep the gates as written and change the representation (ADR 0003): store, per fact and model, a sign-bit code (`varbit`, 128 bytes) in a narrow table that is scanned with an XOR and `bit_count`, and an int8 copy with a scale (`bytea`, 1,024 bytes) in a wide table that is only read for candidates. A search scans the codes, takes the nearest `k × oversample` as candidates, drops those not visible at the asked `valid_at` and `known_at` (widening and rescanning when too few survive), reads the int8 copies of the survivors and ranks them by cosine in the application. Quantization is done in C#; the schema needs no `vector` extension. Why it should hit the numbers: a trial on the same 52,000 rows scanned the codes in 9 to 16 ms and fetched 100 candidates in about 2 ms (8 ms end to end), and the two tables measured 11 MB and about 61 MB (7 rows per page of 1.1 KB).

The cost is recall: the scan ranks by sign agreement, and int8 ranking is near-exact. Recall is measured, not assumed.

## Out of scope

- Embedding the text (consumers pass vectors in), choosing the model, shortened-output (Matryoshka) dimensions.
- Full-text fusion by reciprocal rank, `Explain`, the rest of F3.
- An approximate index (HNSW, IVF). The ADR adds one only if this approach misses.
- Mean-centering of vectors before sign quantization (a later recall improvement if real embeddings need it).

## Acceptance checks

New gate **G3r**: tie-aware recall at 10 of `NearestAsync` against exact cosine over the original float vectors, mean over 200 queries, at least 0.95: the share of results whose exact cosine is within 0.01 of the exact tenth best. It runs in two regimes of a clustered synthetic generator (64 clusters, 1024 dimensions; "tight" noise 0.5, "loose" noise 1.0 relative to the cluster center; queries are fresh draws from the same distribution, so the true neighbours are cluster mates, the hard case). Strict recall (the same ten facts) is reported beside it but not gated: a first measurement showed synthetic neighbours are nearly tied, so strict recall mostly counts swaps of facts whose cosines differ by a hair (ADR 0003 has the numbers). Random uniform vectors are not used: they have no neighbours, so recall on them measures nothing.

| ID | Check | Command | Passes when |
|---|---|---|---|
| V1 | Target-size gates | `dotnet run -c Release --project bench/Switchknife.Facts.Bench -- run --connection "Host=localhost;Database=facts_bench;..." --subjects 2000 --facts 50000 --queries 2000 --episode-size 5 --vector-dims 1024 --vector-noise 1.0 --fresh --gate` (and `--vector-noise 0.5`) | exit 0: `nearest.p95` at most 50 ms, `embeddingBytes` at most 100 MB, `nearestTieRecall` at least 0.95, plus the existing ingest, as-of and size gates |
| V2 | Small-size gate in CI | `dotnet test --project tests/Switchknife.Facts.Bench.Tests --filter-method "*gates_hold_at_5000_facts*"` | the same gates on 5,000 facts at 1024 dimensions in both regimes, latency in the best of three runs |
| V3 | Gate can fail | `dotnet test --project tests/Switchknife.Facts.Bench.Tests --filter-class "*HarnessTests"` | a report with tie-aware recall under 0.95, or nearest over its limit, yields a failure naming the field |
| V4 | Exact when it can be | `Nearest_with_oversample_covering_all_rows_equals_brute_force_cosine_order` (`tests/Switchknife.Facts.Tests`) | with candidates covering every row, the order equals exact cosine on the float originals, apart from int8 rounding that cannot reorder pairs more than `1e-2` apart |
| V5 | Look-ahead | `Nearest_never_returns_a_fact_recorded_after_known_at_and_still_returns_one_retired_after_it` | as the name says, for `known_at` and `valid_at` |
| V6 | Widening | `Nearest_widens_the_candidate_set_when_filters_remove_most_of_it` | 10 results when only 5% of candidates are visible, and fewer only when fewer exist |
| V7 | Quantizer | `QuantizerTests` (no Docker) | sign bits are `x > 0`; int8 round trip error at most `scale / 2` per component; a zero vector, NaN or a dimension change is refused |
| V8 | AOT | `scripts/aot-smoke.sh` | the Facts case also writes two embeddings and finds the nearer one without the JIT |
| V9 | Repository | build with `-warnaserror`, `dotnet format --verify-no-changes`, `dotnet test`, `dotnet pack` | clean |

If a gate cannot be met with this approach, the pull request says so with the measured numbers and proposes the alternative (pgvector's `<~>` and an HNSW index on the codes, or native code for the scan); the thresholds stay.

## Result

G3 and G4 are met at the target size with the thresholds unchanged; numbers, the oversample sweep and the strict-recall figures are in ADR 0003. Summary: nearest p95 261 ms to 21.5 to 50.8 ms over four runs on a loaded laptop (one run over the limit), embeddings 151.7 MB to 73.2 MB, tie-aware recall 1.000, strict recall 0.81 to 0.89.
