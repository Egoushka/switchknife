# Retrieval arms on the K0 corpus: kit, kit + cross-encoder, Graphiti (2026-11-05)

Question: does any of three retrievers find the outside names past decisions considered well enough to build on, with Switchknife.Facts
kept as the store and provenance contract? This is a new knowledge question under D-76 (owner's decisions log), not a fourth K0 run
([ADR 0006](../../adr/0006-k0-stop-on-production-corpus.md)): each arm gets one run, nothing is tuned between runs, and this section was
committed before the first run.

## Method (fixed before the runs)

- **Corpus:** the same 826 live facts of namespace `prior-art` that K0's production run used (the JSON-lines export behind ADR 0006,
  SHA-256 prefix `60c4827ec885`). It stays outside this repository.
- **Gold:** the same 20 decisions and 175 gold names (`prior-art-gold.jsonl`, outside this repository). A name counts when it appears as a
  whole word in what the arm shows in its top 10 (`Decision.Found`, unchanged). recall@10 is the macro mean over the 20 decisions.
- **p95:** 200 timings of one search call: one warm-up pass, then 10 passes of the 20 questions; query vectors already computed; on a
  laptop (Apple M1, 16 GB), databases in local containers.
- **Arms:**
  1. `kit`: `KnowledgeSearch` as is, `text-embedding-3-small` at 1536 dimensions, as in K0. Expected: 0.199 and about 227 ms.
  2. `rerank`: the kit's top 50 entities (each shown as name plus its up to two facts, exactly what `kit` shows) scored by
     `cross-encoder/ms-marco-MiniLM-L6-v2` (fp32 ONNX, revision `233902d`, Apache-2.0) in-process on the CPU with ONNX Runtime; the 10 best
     are shown. The timing covers the kit query and the rerank.
  3. `graphiti`: Graphiti 0.31.2 on its defaults with Neo4j 5.26 Community: `OpenAIClient` (`gpt-6-luna`, its default model),
     `OpenAIEmbedder` (`text-embedding-3-small`, its default 1024 dimensions), `Graphiti.search` (hybrid search fused with RRF, 10 edges).
     One text episode per fact: `"<subject name> (<kind>): <text>"`, the source's time as reference time, added with `add_episode_bulk` in
     chunks of 50, oldest first. Each edge is shown as its source entity's name, its fact, its target entity's name. Graphiti runs locally;
     only the model calls go out, through the same OpenAI-compatible gateway K0 used for vectors.
- **Cost cap:** $5 (starting value). Graphiti's model calls are counted from each response's own token usage and priced at the gateway's
  configured prices: `gpt-6-luna` $0.10 in and $0.50 out, `text-embedding-3-small` $0.02, per million tokens. After the first 50 facts the
  spend is projected to 826; above $5 the Graphiti arm stops and this report says so.

## Decision rule (fixed before the runs)

- A **winner** must beat `kit` by more than 0.10 recall@10. If the best arm beats `kit` by 0.10 or less, the gold set grows to 50 decisions
  before anything is decided.
- The line is D-76's: recall@10 at least 0.32 and p95 at most 150 ms.
- **Outcome**, one of three:
  - **port the technique** into Switchknife.Facts when the winner is `rerank` (an in-process technique on our own store), or when the winner
    is Graphiti and what makes it win can be named and rebuilt on our store;
  - **run the winner as a retrieval sidecar** when the winner is Graphiti and its win comes from the graph it builds (extraction, entity
    resolution), with Switchknife.Facts still the store it is fed from;
  - **stop retrieval work** when no arm reaches 0.32.

## Results

Not run yet.
