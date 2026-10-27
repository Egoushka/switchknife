# 0003. Facts library vectors: sign-bit scan and int8 rerank, no pgvector

- Status: accepted (pre-release schema; the first slice's `fact_embeddings` is replaced, not migrated)
- Date: 2026-10-27
- Changes: the "Embeddings", "Retrieval" and "Memory budget" lines of `docs/adr/0001-facts-library.md`

## Context

ADR 0001 stored one `halfvec` per fact and model and scanned it exactly with pgvector, adding an approximate index only if the exact scan missed. Gates G3 (nearest p95 at most 50 ms) and G4 (embeddings at most 100 MB at 1024 dimensions) were measured on the first slice (52,000 live facts, PostgreSQL 18, container limited to 384 MB, Apple silicon laptop):

| | Threshold | Measured |
|---|---|---|
| nearest, p95 | 50 ms | 261 ms (p50 178) |
| embeddings | 100 MB | 151.7 MB |

Findings behind the numbers (`EXPLAIN` and relation sizes on that data, spec `docs/specs/2026-10-27-facts-vectors.md`):

- The exact scan computes 52,000 half-precision distances of 1024 dimensions in about 180 ms with the data in memory, no join and no TOAST. It is compute- and memory-bound.
- A 1024-dimension `halfvec` is 2,052 bytes, over the 2 KB TOAST threshold: the table is 3.5 MB and its TOAST relation 138 MB. `STORAGE PLAIN` gives 139 MB (three rows per page). The floor for 52,000 rows of 2,052 bytes is 107 MB before page overhead, so no layout of 1024-dimension `halfvec` meets 100 MB; ADR 0001's "about 100 MB" was arithmetic for 50,000 rows.

## Options

1. **Keep `halfvec`, exact scan** (ADR 0001). Cannot meet G4 by arithmetic, and G3 by 3.5x or more. Rejected.
2. **pgvector HNSW or IVF index.** The index stores the vectors again, so G4 gets worse (about 2x), recall under the as-of filters needs iterative scans, and it needs the `vector` extension. Not measured; kept as the fallback if option 4 misses.
3. **Fewer dimensions** (768 or 512, if the consumer's model supports shortened output). Works for size (about 85 MB at 768), does not touch G3, depends on a model property that is UNVERIFIED for Nytka's model. Remains available to a consumer on top of this decision.
4. **Sign-bit codes scanned exactly, int8 vectors for ranking** (chosen). Per fact and model: a `varbit` of one sign bit per component (128 bytes at 1024) in a narrow table `fact_embeddings`, and the unit vector as int8 (1,024 bytes, no scale kept because cosine ignores it) in `fact_vectors`. A search scans the codes with `bit_count(bits # query)`, takes `k × oversample` candidates, drops those not visible at the asked instants (widening the scan fourfold when too few survive), then ranks the survivors by cosine on their int8 vectors in the application.
5. **Native code** (a Rust or C scan, an extension). Not needed at these numbers; the scan is 10 to 15 ms in SQL.

## Decision

Option 4. Quantization is done in C#, so the schema needs only `btree_gist`: no `vector` extension, the question ADR 0001 left open about creating it without a superuser disappears, and any PostgreSQL 14 or later (`bit_count`) works. The exact int8 pass keeps ranking faithful; the loss is in candidate selection, which is measured, not assumed.

Two details that cost time and are recorded so they are not rediscovered:

- Candidate rows are fetched with two lookups by id array (visibility on `facts`, then `q8` on `fact_vectors`), not a join. With a join the planner drove from the `facts` filter and probed the vectors once per matching fact: 21 ms for 400 candidates on a 1,000-fact table, growing with the table, and it looked like a slow-musl-image problem until `auto_explain` showed the plan.
- Recall is reported two ways. Strict (the same ten facts as exact cosine) punishes swapping two facts whose cosines differ by a hair, and synthetic neighbours are nearly tied. Tie-aware (the share of results whose exact cosine is within 0.01 of the exact tenth best) is the gate; strict is reported beside it.

## Measured

50,000 facts requested (52,000 live rows), 2,000 subjects, 1024 dimensions, 64 clusters, 200 queries, oversample 40, the production pgvector image (only its Postgres binaries are used) in a 384 MB container; the laptop's load average was between 15 and 38 from other work, so latency is a range:

| | Threshold | Before | After |
|---|---|---|---|
| nearest p95, ms | 50 | 261 | 21.5, 31.2, 37.7, 50.8 over four runs (p50 14 to 22) |
| embeddings, MB | 100 | 151.7 | 73.2 (codes about 11, int8 about 62) |
| tie-aware recall at 10 | 0.95 | 1.0 (exact) | 1.000 in every run |
| strict recall at 10 | reported | 1.0 | 0.89 loose clusters (noise 1.0), 0.81 tight (noise 0.5) |

Oversample sweep, loose clusters, same data (earlier candidate query): strict recall 0.47 at 10, 0.90 at 40, 0.99 at 100; p95 latency 48, 40, 52 ms. 40 is the default: the largest value that keeps p95 well inside the gate on this hardware. One of four final runs exceeded 50 ms (50.8) while the machine was loaded; the in-repository gate takes the best of three runs for that reason.

## Consequences

- `fact_embeddings` of the first slice (`halfvec`) is replaced in `0001_facts.sql` itself. Nothing was released or tagged, so there is no installed schema to migrate; from the first `facts/v` tag migrations are forward-only again.
- A model has one dimension; a different query length is a PostgreSQL error (22026) at search time.
- Search is approximate by design. A consumer needing exact results sets `oversample` to cover all rows (a test does, and equals brute force) at the cost of ranking every row.
- Raw sign bits assume vectors roughly centred on zero, as common text embeddings are. If real embeddings are not, mean-centring before quantization is the first thing to try; it is not implemented.
- Memory: 73 MB at 52,000 facts and 1024 dimensions, inside ADR 0001's 150 MB total for both consumers.

## Reopen if

- Recall measured on real Nytka or feed-eater embeddings is below the synthetic numbers (the labelled set of gate G0 includes a retrieval question: "Retrieval questions" in `docs/benchmarks/facts-library.md`).
- A consumer needs more than about 300,000 live facts per model: the scan is linear (about 0.25 ms per 1,000 rows here); then pgvector's `<~>` (measured 9 ms against 12 to 16 ms for the builtin on 52,000 rows) or an HNSW index on the codes.
