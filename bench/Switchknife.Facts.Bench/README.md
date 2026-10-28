# Switchknife.Facts.Bench

Measures [`Switchknife.Facts`](../../src/Switchknife.Facts) (design: [`docs/adr/0001-facts-library.md`](../../docs/adr/0001-facts-library.md)) against PostgreSQL. Not a package.

- `Runner.cs` drives the library's own `FactsStore` (ingest, as-of reads, embeddings, `NearestAsync`), so the numbers measure the code consumers run. The schema is the library's embedded `Migrations/0001_facts.sql`.
- `Probes.cs` is what the library does not offer yet, as plain SQL: full text, the live-fact list, relation sizes. `VectorWorkload.cs` makes clustered embeddings (uniform random vectors have no neighbours); `run --vector-dims 1024` embeds every live fact and measures `NearestAsync` latency and recall (strict and tie-aware) against exact cosine over the original floats; `--vector-noise` (1.0 loose, 0.5 tight) and `--oversample` (40) set the regime.
- `Gates.cs` holds the ADR's thresholds (ingest p95 30 ms, as-of p95 10 ms, nearest p95 50 ms, facts 150 MB, embeddings 100 MB, tie-aware recall 0.95); `run --gate` exits 3 when a run exceeds one.
- `Labelling.cs` and `LabelCommand.cs` are the `label` verbs that build the G0 labelled set: import episodes from the tools' JSON, plant edited copies, write the extraction kit's prompt, turn model output into a draft, mark review edits, find supersession pairs and check the set.
- `Workload.cs` generates a deterministic history with late and out-of-order facts, and answers every as-of question itself. A store that disagrees is wrong.

```bash
# mechanics: the database name must contain "bench"; the run replaces the facts schema
dotnet run -c Release --project bench/Switchknife.Facts.Bench -- run \
  --connection "Host=localhost;Database=facts_bench;Username=postgres;Password=..." \
  --subjects 2000 --facts 50000 --queries 2000 --episode-size 5 --vector-dims 1024 --fresh --gate

# extraction quality against a labelled set kept outside this repository
dotnet run -c Release --project bench/Switchknife.Facts.Bench -- score --gold gold.jsonl --pred predicted.jsonl

# drawing the labelled set: import, plant, prompt, draft, edits, pairs, check
dotnet run -c Release --project bench/Switchknife.Facts.Bench -- label check --episodes episodes.jsonl --gold gold.jsonl
```

`run` prints one JSON report and exits 1 when any as-of answer disagrees with the oracle. What each field means, the sizes to run, the labelled-set format and the budget table are in [`docs/benchmarks/facts-library.md`](../../docs/benchmarks/facts-library.md).

Needs a PostgreSQL 18 server with the `btree_gist` extension (any official image has it; the library needs no other extension). The real conversations and articles behind the labelled set never go in this repository.
