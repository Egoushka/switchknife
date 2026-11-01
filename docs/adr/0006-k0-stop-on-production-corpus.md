# 6. K0 stops: one run on the production corpus, fixed line, STOP

Status: accepted

## Context

K0 (retrieval repair, [ADR 0005](0005-knowledge-kit.md)) failed its recall gate three times on corpora rebuilt locally (0.145, 0.183, 0.234). The owner decided on 2026-11-01 that the local runs had not tested the idea and fixed one last run on the production `prior-art` corpus, with the line written before the run ([#43](https://github.com/Egoushka/switchknife/issues/43)):

- **Continue** if kit recall@10 is at least 0.32 (lenient, 20-decision gold set, 175 gold names) **and** p95 is at most 150 ms.
- **Anything else is STOP.** No fourth run, no tuning between runs.

The line is stricter than ADR 0005's K0 row, which stops only below 0.27.

## Run

One run, 2026-11-01, `bench recall --systems b0,kit`:

- **Corpus:** all 826 live facts of namespace `prior-art` in feed-eater's database on cobalt (`facts.facts`, `retired_at is null`), exported read-only with `lab sql` into a JSON-lines file and loaded into a throwaway PostgreSQL 18 container. Nothing was written to the box.
- **Gold:** `context/ai-setup/eval/prior-art-gold.jsonl`, 20 decisions, 175 gold names.
- **Vectors:** `text-embedding-3-small`, 1536 dimensions, through the box's Bifrost gateway. The default size, as in the earlier 1536 runs.
- **p95:** 200 timings of one search call (one warm-up pass, then 10 passes of the 20 questions), query vectors already computed, local container. The harness did not print p95 for `recall` before this run; the timing was added to the K0 bench (PR #40) before the run and changes no retrieval.

| System | recall@10 (macro) | micro | Wilson 95% | p95 | p50 |
|---|---|---|---|---|---|
| `b0` plain text | 0.123 | 22/175 | 0.085 to 0.183 | 194.2 ms | 14.8 ms |
| `kit` | **0.199** | 34/175 | 0.142 to 0.259 | **227.4 ms** | 91.3 ms |

The gold names that appear anywhere in the 826 facts (ceiling, macro): 0.490. Raw result: [`2026-11-01-knowledge-k0-prod.json`](../benchmarks/results/2026-11-01-knowledge-k0-prod.json).

## Decision

**STOP.** Both conditions fail: recall 0.199 against 0.32 (0.121 short; the whole Wilson interval is below 0.32), and p95 227.4 ms against 150 ms. The kit beats `b0` by 0.076 where the gate asked for 0.15.

- Agent-facing retrieval on the kit does not ship. Agents keep `rg` and `feed_search`. This is the stop condition of ADR 0005 ("the kit continues without agent-facing retrieval").
- No fourth run and no tuning of this retriever. A different design (for example entity resolution, Facts F4) is a new experiment: it needs its own issue, its own gate written first, and is not a rerun of this one.
- No K1 issue is opened from this result. ADR 0005 lists K1 as depending on "K0 passed or stopped"; what K1 contains now is the owner's call.

## What the run showed

- **The production corpus did not lift recall.** The kit scored 0.199 here and 0.196 in the local rebuild at the same size (1536). The idea that production would score higher rested on its ceiling, 0.641 against 0.476 locally. The 0.641 in ADR 0005 counts gold names anywhere in the written research; measured the way the bench measures it (fact text and subject name), the production ceiling is 0.490, close to the local 0.476. The two figures are different measures, so the "production is richer" premise did not hold for facts.
- **Latency misses even at 826 facts.** 227.4 ms is over 150 ms on 826 facts; the gate asks for five namespaces of 50,000. The earlier tiled words-only figure was 844 ms.
- **Limits:** one corpus, one embedding model and size, a kit that groups by subject entity with no resolution across sources, p95 on a laptop container and not on the target database.
