# K0: retrieval on `prior-art`, pass and stop line

Written before any number of the repaired retriever was measured. Design: [`docs/adr/0005-knowledge-kit.md`](../adr/0005-knowledge-kit.md), stage K0.

## What is measured

- **Gold set**: 20 past decisions, 175 gold names, each question written without its answer (kept outside this repository; `bench recall --gold <path>` reads it). A name counts as found when it appears as a whole word, with space, hyphen and underscore alike, in the text a system shows for its top 10 results (the matching rule of feed-eater's `PriorArt.Bench`, so numbers stay comparable).
- **Result unit**: B0 and the fact-level system return 10 facts. The kit returns 10 **entities** (tool, problem, approach). What it shows per entity is fixed here, so the score cannot grow by showing more text: the entity's name and its best 2 facts, each at most 300 characters (the Facts column limit). A second figure, `name only`, counts a gold name only when it is the name of a returned entity; it is reported, not gated.
- **Score**: recall@10, mean over decisions of found names over gold names (macro), with micro and Wilson 95% interval, as in the 2026-10-29 runs.
- **Corpus**: the prior-art namespace rebuilt locally by feed-eater's own importers from the same files (`ai-setup/audits/*.md`, `research/*/notes/*.md`). Prod holds 826 live facts; the rebuild holds fewer because chargehand results and the snapshot CSV are not available outside prod. Every report states its fact count.
- **Embeddings**: the harness has no embedding credentials. Systems run on words alone unless `--vectors <file>` supplies question and fact vectors. A run without them says `vectors: none` and is a lower bound for the kit.

## Gate (from the ADR, unchanged)

| Check | Pass | Stop |
|---|---|---|
| recall@10 lenient, kit, gold set above | at least 0.32 (B0 0.169 + 0.15) | under 0.27 (B0 + 0.10): agent-facing retrieval stops |
| `knowledge_search` p95, 5 namespaces of 50,000 facts, in the database | at most 150 ms | over 150 ms: prefilter by entity, then lower `oversample`; never a bigger container |

The gate is not moved to fit a result. If the kit lands between 0.27 and 0.32 the stage neither passes nor stops, and the report says so.

## Systems

- `b0`: plain text. Each fact's text and its source file name scored by overlap with the question's words (idf-weighted), top 10 facts. A stand-in for `rg` plus `feed_search`; the 0.169 of the 2026-10-29 run stays the reference figure.
- `facts`: `FactsStore.SearchAsync` as feed-eater calls it, top 10 facts (the 0.145 and 0.183 runs).
- `kit`: `Switchknife.Knowledge`: per-namespace fan-out, idf-weighted OR full text, entity-level grouping, reciprocal-rank fusion across namespaces.

## Results, 2026-11-01 (words only, no vectors)

Corpus: 711 live facts (prod 826), 20 decisions, 175 gold names. The corpus alone contains 47.6% of the gold names (the ceiling for any system on it; prod's is 64.1%).

| System | recall@10 macro | Note |
|---|---|---|
| `b0` plain text | 0.140 | the 0.169 of 2026-10-29 is the reference |
| `facts` | 0.000 | `plainto_tsquery` is an AND of every word: a sentence matches no fact; in prod only the vector list answers |
| `kit` | 0.128 | micro 23 of 175 (0.131), Wilson 95% 0.089 to 0.189; `name only` 0.046 |

Latency, `bench latency --namespaces 5 --facts 50000` (250,000 facts, tiled copies of the 711, 200 queries, words only, PostgreSQL in a local container): **p50 605 ms, p95 844 ms**, gate 150 ms.

## Verdict

- **Recall: below the stop line (0.27) on words alone, and not decided.** Without vectors this is a lower bound for the kit, and the corpus is smaller than prod's. The vector path is UNMEASURED: no embedding credentials were available to the harness. The kit does not beat B0 on words, so entity grouping and OR search alone do not clear the gate. The gate is not moved.
- **Latency: over the gate.** The OR query matches many facts per term, so the ranking step dominates. Tiling repeats each text 70 times per namespace, which raises how many facts each term matches; a real 50,000-fact namespace may be faster. Per the ADR, the next step is the per-namespace prefilter by entity, then a lower `oversample`.
- **Needed to decide:** `bench recall --vectors <file>` with question and fact vectors from the production embedder (the harness reads them; producing them needs the embedding credentials), run on the prod corpus.
