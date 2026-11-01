# K0: retrieval on `prior-art`, pass and stop line

Written before any number of the repaired retriever was measured. Design: [`docs/adr/0005-knowledge-kit.md`](../adr/0005-knowledge-kit.md), stage K0.

## What is measured

- **Gold set**: 20 past decisions, 175 gold names, each question written without its answer (kept outside this repository; `bench recall --gold <path>` reads it). A name counts as found when it appears as a whole word, with space, hyphen and underscore alike, in the text a system shows for its top 10 results (the matching rule of feed-eater's `PriorArt.Bench`, so numbers stay comparable).
- **Result unit**: B0 and the fact-level system return 10 facts. The kit returns 10 **entities** (tool, problem, approach). What it shows per entity is fixed here, so the score cannot grow by showing more text: the entity's name and its best 2 facts, each at most 300 characters (the Facts column limit). A second figure, `name only`, counts a gold name only when it is the name of a returned entity; it is reported, not gated.
- **Score**: recall@10, mean over decisions of found names over gold names (macro), with micro and Wilson 95% interval, as in the 2026-10-29 runs.
- **Corpus**: the prior-art namespace rebuilt locally by feed-eater's own importers from the same files (`ai-setup/audits/*.md`, `research/*/notes/*.md`). Prod holds 826 live facts; the rebuild holds fewer because chargehand results and the snapshot CSV are not available outside prod. Every report states its fact count.
- **Embeddings**: with `KNOWLEDGE_EMBED_URL` and `KNOWLEDGE_EMBED_KEY` in the environment (an OpenAI-compatible endpoint; the key never reaches a file or an argument) `bench recall` embeds the facts and the questions; `--model` and `--dimensions` choose the model and size. Without them the run says `vectors: none` and is words only, a lower bound for the kit.

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

## Results, 2026-11-01

Corpus: 711 live facts (prod 826), 20 decisions, 175 gold names. The corpus alone contains 47.6% of the gold names (the ceiling for any system on it; prod's is 64.1%). Vectors: `text-embedding-3-small`, the model feed-eater uses.

| System | words only | vectors, 1536 dimensions | vectors, 768 dimensions |
|---|---|---|---|
| `b0` plain text | 0.140 | 0.140 | 0.140 |
| `facts` (`FactsStore.SearchAsync`) | 0.000 | 0.188 | 0.177 |
| `kit` | 0.128 | **0.196** (micro 35/175, Wilson 95% 0.147 to 0.265) | **0.234** (micro 38/175, Wilson 0.162 to 0.284) |

`facts` with vectors (0.188) matches prod's own 0.183 of 2026-10-29, so this corpus and these vectors behave like prod's. Without vectors `facts` finds nothing: its text list is an AND of every word (`plainto_tsquery`), so a sentence matches no fact.

Latency, `bench latency --namespaces 5 --facts 50000` (250,000 facts, tiled copies of the 711, 200 queries, words only, PostgreSQL in a local container): **p50 605 ms, p95 844 ms**, gate 150 ms.

## Verdict

- **Recall: STOP.** The kit's best figure, 0.234, is under the stop line (0.27) and 0.09 under the pass line (0.32); the harness prints STOP at both sizes. Its Wilson interval (0.162 to 0.284) is mostly under the stop line. Against `facts` the kit adds 0.01 (1536) to 0.06 (768); against `b0` 0.06 to 0.09, where the gate asked for 0.15. Entity grouping and OR search help, but nowhere near the margin. The gate is not moved.
- **Latency: over the gate** (844 ms against 150 ms). Tiling repeats each text 70 times per namespace, which raises how many facts each term matches; a real 50,000-fact namespace may be faster. The latency run has no vectors.
- **Embedding size (ADR decision 7):** 768 scored higher than 1536 for the kit (0.234 against 0.196) and lower for `facts` (0.177 against 0.188). Both differences are inside the interval at 175 gold names, so this run does not pick a size; it does not show 768 losing more than 0.02.
- **Limits:** one corpus of 711 facts, one embedding model, and a kit that groups by subject entity only (no entity resolution across sources, Facts F4, which the ADR names as a K0 dependency). A design with entity resolution is a new experiment, not a lowered gate.
