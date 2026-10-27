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

## Retracting, trusting, changing sources

```csharp
await facts.IngestAsync(new NewEpisode("web", "page-9", hash, Trust: EpisodeTrust.ThirdParty), [...]);   // may add and corroborate, never retire an owner's fact
await facts.IngestAsync(new NewEpisode("note", "note-1", newHash), [...]);   // a new hash for the same source: facts only the old text supports retire (source_changed)
await facts.RetractAsync(factId);   // retired as retracted; the same assertion is never written again
await facts.RestoreAsync(factId);   // lifts the block, writes the rows again (throws if a newer fact took the interval)
new NewFact(anna, "lives_in", "Kyiv", from, ValidTo: to)   // a closed range on a One predicate: the earlier value resumes after it
new NewFact(anna, "works_at", null, from, Text: "Anna works at Acme", ObjectEntity: acmeId)   // the object is an entity
```

A fact is as trusted as its most trusted source. A lower-trust value that would retire a higher-trust one is stored already retired (`outranked`), so it shows in `HistoryAsync` and never in `AsOfAsync`. Retracting a `One` value leaves a gap, not the value it had clipped. Returning to an older content hash counts as the same episode again and does nothing; give reverted text a new hash.

## Deleting a source for privacy

```csharp
var result = await facts.PurgeSourceAsync("conversation", "c-42");   // PurgeResult: episodes, assertions gone, slots rebuilt
var problems = await facts.VerifySlotAsync(anna, "lives_in");        // empty: the stored rows are what replaying the history produces
```

Purge deletes every version of the source, the facts only it supports (with their embeddings) and its source lines on the rest, then rewrites each single-valued slot it touched by replaying what remains, in recorded order, through the write path itself, so the slot equals one that never saw the source. It is the one operation that deletes rows; rows of a rewritten slot get new ids, and embeddings follow the assertion. `VerifySlotAsync` does the same replay in a transaction it rolls back and lists the rows that differ.

## Explaining a fact

```csharp
var why = await facts.ExplainAsync(factId);   // FactExplanation, or null for an id this namespace does not hold
// why.Sources: episode, source kind and ref, content hash, locator, quote, trust; why.Trust: the highest of them
// why.Version: validity range, recorded_at, retired_at, retired_reason; why.Retraction: when retracted, and restored if so
// why.Successors: the rows that replaced this one, nearest first; why.Predecessors: the rows it replaced
```

One round trip. The library never holds your text: hand `SourceKind`, `SourceRef` and `LocatorJson` to your own resolver to get a link or a snippet. The extractor and model are not reported yet (nothing writes them until the extraction contract).

## Finding facts by meaning

```csharp
await facts.SetEmbeddingsAsync("my-model", [new FactEmbedding(factId, vector)]);   // your embedder's output, any length, one length per model
var near = await facts.NearestAsync("my-model", queryVector, k: 10, validAt: now, knownAt: null);   // ScoredFact: the fact and its cosine
```

Vectors are stored compressed (a sign-bit code to scan, an int8 copy to rank, ~1.4 KB per fact at 1024 dimensions; [ADR 0003](../../docs/adr/0003-facts-vector-storage.md)), so no PostgreSQL extension is needed beyond `btree_gist`. Search honours `validAt` and `knownAt` like `AsOfAsync`. It is approximate: `oversample` (default 40) candidates per result are ranked exactly; raise it for recall, or to cover every row for exact results. At 52,000 facts and 1024 dimensions: p95 21 to 51 ms, 73 MB, results within 0.01 cosine of the exact top ten.

## Searching by words and meaning

```csharp
var hits = await facts.SearchAsync("jazz concert", queryVector, "my-model", limit: 10, validAt: now);   // text, vector or both
// hit.Fact, hit.Score, hit.TextRank, hit.VectorRank (null: that list did not hold it), hit.Similarity
```

A full-text list (`simple` configuration, ranked by `ts_rank_cd`) and a nearest-neighbour list run at once, each `limit × 4` deep, and are fused by reciprocal rank: a fact scores `1 / (60 + rank)` summed over the lists that hold it, so a fact in both beats one that leads a single list. Both obey `validAt` and `knownAt`; retired and other-namespace facts never appear. Pass a blank text or an empty vector to search with one list only. The ranks on each hit say why it is there.

## Not in this slice

Full-text/vector fusion, entity resolution and merge, the extractor contract, tool definitions. See [the write-path spec](../../docs/specs/2026-10-28-facts-write-path.md) and the slice table for the order.

## Measured

`bench/Switchknife.Facts.Bench` drives this package against PostgreSQL 18 in a 384 MB container: 50,000 facts, 5 per episode, ingest p95 about 14 ms, as-of p95 under 0.4 ms, 57 MB of table and indexes, no wrong answer against its oracle. Thresholds and method: [`docs/benchmarks/facts-library.md`](../../docs/benchmarks/facts-library.md).

Native AOT: `samples/Switchknife.AotSmoke` publishes with Native AOT, warnings as errors, and runs a superseding pair and an as-of read against a real PostgreSQL.
