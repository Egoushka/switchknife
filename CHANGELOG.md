# Changelog

Each package is versioned on its own by a tag `<package>/v<version>`. Entries are per package, newest first.

## Switchknife.Facts.Testing 0.1.0 (2026-10-28)

First release (#30). `ScriptedExtractor`, an `IFactExtractor` that replays queued model replies or errors and records each request in `Requests`, so code that extracts facts runs through the full `ExtractAsync` pipeline without calling a model. No test-framework dependency, Native AOT compatible. Depends on Switchknife.Facts 0.1.0.

## Switchknife.Facts 0.1.0 (2026-10-28)

First release. Bi-temporal facts in your own PostgreSQL, designed in [ADR 0001](docs/adr/0001-facts-library.md).

- **F1, typed core** (#16): episodes, entities, cardinality-typed predicates (`One`, `Many`, `Event`) that supersede by clipping, `AsOfAsync` ("true at T, as known at K") and `HistoryAsync`, an exclusion constraint for one live value per slot, embedded schema for `Switchknife.Migrations`, Native AOT smoke case.
- **Vector storage** (#17, #18, #19): `SetEmbeddingsAsync` and `NearestAsync` on a sign-bit scan with an int8 rerank, no `vector` extension needed (ADR 0003); gates G3 and G4 held in 10 of 10 runs.
- **F2, write path** (#20, #21): closed ranges, entity objects, retraction, trust, changed sources; purge of a source by slot replay with a replay integrity check.
- **F3, retrieval** (#22, #23, #24): `ExplainAsync` (why a fact is believed: its sources with extractor and model, its retraction, and the rows it replaced or was replaced by) and `SearchAsync`, full-text and vector search fused by reciprocal rank.
- **F5, extraction** (#25, #26, #27): a provider-neutral extraction kit (prompt, JSON schema, parser, validator), `ExtractAsync` and `IngestExtractedAsync`, failed episodes recorded with a code, extractor and model stored on the episode.

Measured at 50,000 facts on PostgreSQL 18 in a 384 MB container: nearest p95 27.5 ms (limit 50), embeddings 76.8 MB (limit 100), ingest p95 21.2 ms (limit 30), on a loaded laptop. The F2, F3 and F5 pull requests list a one-line mutation that turns each new test red (23 of 23 in #27).

Not in this release: entity resolution and merge (F4), tool definitions (F6). The scripted extractor ships as `Switchknife.Facts.Testing`.
