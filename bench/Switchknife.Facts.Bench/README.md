# Switchknife.Facts.Bench

Measures the design in [`docs/adr/0001-facts-library.md`](../../docs/adr/0001-facts-library.md) against PostgreSQL before any library exists. Not a package.

- `Schema/0001_facts.sql` is the schema the library will ship. Tests apply it, so the ADR cannot drift from it.
- `FactsStore.cs` is the write path (supersession by clipping, immutable rows, sources) and the read paths (as of a valid and a transaction time, full text, exact vector scan) as plain SQL. The library is built from it.
- `Workload.cs` generates a deterministic history with late and out-of-order facts, and answers every as-of question itself. A store that disagrees is wrong.

```bash
# mechanics: the database name must contain "bench"; the run replaces the facts schema
dotnet run -c Release --project bench/Switchknife.Facts.Bench -- run \
  --connection "Host=localhost;Database=facts_bench;Username=postgres;Password=..." \
  --subjects 2000 --facts 50000 --queries 2000 --episode-size 5 --vector-dims 1024 --fresh

# extraction quality against a labelled set kept outside this repository
dotnet run -c Release --project bench/Switchknife.Facts.Bench -- score --gold gold.jsonl --pred predicted.jsonl
```

`run` prints one JSON report and exits 1 when any as-of answer disagrees with the oracle. What each field means, the sizes to run, the labelled-set format and the budget table are in [`docs/benchmarks/facts-library.md`](../../docs/benchmarks/facts-library.md).

Needs a PostgreSQL 18 server with the `vector` and `btree_gist` extensions (the `pgvector/pgvector:0.8.7-pg18-trixie` image has both). The real conversations and articles behind the labelled set never go in this repository.
