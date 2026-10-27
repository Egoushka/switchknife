# Switchknife.Facts

Bi-temporal facts in your own PostgreSQL. A fact carries two timelines: when it was true in the world (`valid`) and when your database learned it (`recorded_at`, `retired_at`). Rows are never edited; a new value ends the old one by retiring it and writing it again, so both "what was true on 1 March" and "what did we believe on 1 March" have an answer. Design and reasons: [`docs/adr/0001-facts-library.md`](../../docs/adr/0001-facts-library.md). **Preview**: the typed core and vector search ([core spec](../../docs/specs/2026-10-27-facts-core.md), [vector spec](../../docs/specs/2026-10-27-facts-vectors.md)); no release yet.

```csharp
// Once per database: run Migrations/0001_facts.sql with your migrator, or
//   new DatabaseMigrator(connectionString, FactsSchema.Scripts, logger).Run();   // Switchknife.Migrations; needs the btree_gist extension
var facts = new FactsStore(dataSource, "my-app");                       // namespace; TimeProvider optional: the ingest clock
await facts.DefinePredicateAsync("lives_in", Cardinality.One);          // one value at a time; Many coexist; Event is a point in time
var anna = await facts.EnsureEntityAsync("person", "Anna", externalRef: "app:people/7");   // the same ref never makes a second entity

await facts.IngestAsync(new NewEpisode("note", "note-1", contentHash),
    [new NewFact(anna, "lives_in", "Kyiv", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))]);
await facts.IngestAsync(new NewEpisode("note", "note-2", otherHash),
    [new NewFact(anna, "lives_in", "Lviv", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero))]);

var inApril = await facts.AsOfAsync(anna, "lives_in", validAt: april1);                    // Lviv
var asBelievedInFebruary = await facts.AsOfAsync(anna, "lives_in", april1, knownAt: feb1); // Kyiv
var rows = await facts.HistoryAsync(anna, "lives_in");   // the retired Kyiv row, its narrowed copy, and Lviv
```

## What it guarantees

- **Immutable rows.** Only `retired_at`, `retired_reason` and `superseded_by` are written after the insert. A `One` value beginning inside a live row retires that row and writes it again ending where the new value begins; a value learned late ends where the next known one starts and retires nothing.
- **One live value per slot, even for raw SQL.** An exclusion constraint (`btree_gist`) refuses two live, overlapping rows for one subject and predicate. The library also serializes writers of a slot with a transaction-scoped advisory lock, so racing episodes do not fail with it.
- **The ingest clock is the only `recorded_at`.** An episode's own date never lowers it, `AsOfAsync` defaults `knownAt` to the clock, and a future `knownAt` throws.
- **Idempotent episodes.** The same `(source kind, source ref, content hash)` is a no-op that reports `Duplicate`. The same assertion again adds a source (`Corroborated`) instead of a row.
- **Your transaction.** `IngestAsync(connection, transaction, …)` writes inside it; the model call (yours) stays outside.
- **Namespaces.** Every row carries the store's namespace; one store never reads another's facts.
- Instants are kept to the microsecond, as PostgreSQL keeps them.

## Finding facts by meaning

```csharp
await facts.SetEmbeddingsAsync("my-model", [new FactEmbedding(factId, vector)]);   // your embedder's output, any length, one length per model
var near = await facts.NearestAsync("my-model", queryVector, k: 10, validAt: now, knownAt: null);   // ScoredFact: the fact and its cosine
```

Vectors are stored compressed (a sign-bit code to scan, an int8 copy to rank, ~1.4 KB per fact at 1024 dimensions; [ADR 0003](../../docs/adr/0003-facts-vector-storage.md)), so no PostgreSQL extension is needed beyond `btree_gist`. Search honours `validAt` and `knownAt` like `AsOfAsync`. It is approximate: `oversample` (default 40) candidates per result are ranked exactly; raise it for recall, or to cover every row for exact results. At 52,000 facts and 1024 dimensions: p95 21 to 51 ms, 73 MB, results within 0.01 cosine of the exact top ten.

## Not in this slice

`ValidTo` on a `One` predicate (it throws), entity-valued objects, retraction, trust, changed-source retirement and purge, `Explain` and search, Explain and full-text/vector fusion, entity resolution and merge, the extractor contract, tool definitions. See the spec for the order.

## Measured

`bench/Switchknife.Facts.Bench` drives this package against PostgreSQL 18 in a 384 MB container: 50,000 facts, 5 per episode, ingest p95 about 14 ms, as-of p95 under 0.4 ms, 57 MB of table and indexes, no wrong answer against its oracle. Thresholds and method: [`docs/benchmarks/facts-library.md`](../../docs/benchmarks/facts-library.md).

Native AOT: `samples/Switchknife.AotSmoke` publishes with Native AOT, warnings as errors, and runs a superseding pair and an as-of read against a real PostgreSQL.
