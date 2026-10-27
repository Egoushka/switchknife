# 0001. A facts library: bi-temporal facts in the consumer's PostgreSQL

- Status: proposed (vector storage superseded by `0003-facts-vector-storage.md`: sign-bit scan and int8 rerank instead of pgvector `halfvec`)
- Date: 2026-10-26

## Context

Two projects keep "what is true about someone or something" and both do it by hand.

- **Nytka** (a voice-capture server) extracts lasting facts from conversations with one model call per conversation. A fact is a sentence in `memories` or `person_facts`, with a `fingerprint`, a tombstone (`deleted_at`), a `replaces` id the model may return for one of the 200 newest memories, and an `expires_at`. Supersession and expiry exist, but a replaced memory is edited or hidden, not kept as a version, and nothing answers "what did I believe on 1 October". Source: Nytka `db/migrations/0006_memories.sql`, `0016_person_facts.sql`, `0042_salience.sql`, `0044_grounding.sql`, and `src/Nytka.Server/Memories/ExtractMemoriesHandler.cs`.
- **feed-eater** (a reading assistant) keeps items, notes and a library, and sends a weekly summary to an external memory service. It extracts no entities or facts. Its workbench plan defers entity extraction until a shared facts library exists.

Volume is small. Nytka held 277 conversations in the 30 days to 2026-10-25 (its quality metrics). Total facts: UNKNOWN (counting them is step 0 of `docs/benchmarks/facts-library.md`). At that rate a store never exceeds tens of thousands of facts within years, so scale is not the risk. The risks are extraction quality, wrong merges and invalidations, privacy deletion, and the home server's memory: 2,089 MB available on 2026-10-26 (node exporter `MemAvailable`), the Nytka database container capped at 384 MB, and its embedding service at 3 GB.

The idea under test: a Graphiti-style temporal knowledge graph on PostgreSQL with pgvector, about one model call per ingested episode to extract entities and edges, bi-temporal edges (valid time and transaction time) so a new fact invalidates the old one, exposed as a library plus MCP tools (add episode, query facts as of a date, explain provenance, invalidate), adoptable by both projects without a second copy.

An earlier review of the memory stack (the workspace's `ai-setup` audit of 2026-09-30) found no new memory product worth adding: Mem0, Graphiti, Letta, Cognee and Supermemory would each be a ninth store, and eight were already too many. It judged Graphiti's bi-temporal edges "the one idea Hindsight lacks natively". This ADR must therefore show that a library inside each consumer's own database is not a ninth store, and that it beats doing nothing.

## What the idea gets right and wrong

| Part of the idea | Verdict | Why |
|---|---|---|
| Bi-temporal facts, nothing overwritten | Keep, and make stricter | Fact rows are immutable; only `retired_*` is written later. "Close the old edge in place" answers "what was true" but not "what did we believe then"; the stricter form answers both. |
| About one model call per episode | Keep as an upper bound; spend zero extra on Nytka | Nytka already pays one call per conversation. The extraction contract widens that call (triples, dates, `replaces`, evidence ids) instead of adding a second. |
| A graph database or graph model | Cut | The queries are "facts about X as of T" and "where did this come from". Tables and a join cover them. Multi-hop traversal is UNKNOWN as a need; a recursive query covers it if the labelled questions ask for it. |
| Entity summaries and communities | Cut | Derived text with its own staleness. Build it lazily from facts when something reads it. |
| Cosine, then BM25, then model for entity resolution | Change | The sibling project `chronicle` measured that the BM25 stage fails across Cyrillic and Latin spellings of one name (`ADR-002-entity-resolution.md` there). Use its phonetic and consonant-skeleton keys as candidate generators. |
| Contradiction found by a model comparing every new edge with old ones | Change | Typed predicates carry a cardinality and invalidate by rule, with no call. Free-text facts reuse what Nytka already does: show the model the live candidates and accept the `replaces` id it returns only if that fact is live and shares the subject. |
| pgvector | Keep, exact scan first | Same argument as chronicle's ADR-001: tens of thousands of rows scan exactly in milliseconds, and an approximate index silently loses recall under filters. |
| Look-ahead safety | Add as a test catalog | TradingAgents v0.4 and v0.5 fixed seven look-ahead leaks (vintages, windows, filing dates, defaulted dates); the spec turns each into a test (L1 to L7). |
| MCP tools | Keep as definitions, not a server | A new server adds a process, a port and an auth surface. Consumers already host MCP endpoints and guard them. |
| A new store | Reject | Facts live in each consumer's database, in their own schema, next to the rows they point at. No new container. |

## Options

1. **A. .NET library on PostgreSQL 18 with pgvector, inside each consumer.** Per-consumer schema `facts`, embedded migrations, extraction supplied by the consumer behind an interface. Recommended.
2. **B0. Do nothing new.** Add `valid_from`, `valid_to` and `recorded_at` to Nytka's `memories`; feed-eater gets its own table later. No shared code, so two copies by definition. This is the baseline A must beat.
3. **B1. Raw episodes, reason at read time.** Store timestamped episodes with their embeddings and no extracted facts; at question time retrieve the episodes as of a date and let a model decide what is current. Zero model calls at write, no invalidation logic, but the answer is nondeterministic, costs a call per question, and "invalidate" has nothing to act on. It is how `chronicle` and Hindsight lean.
4. **C. SQLite per consumer (Switchknife.Journal's engine) with sqlite-vec.** No load on PostgreSQL, Native AOT, trivial tests. But facts could not join `people`, `conversations` or `items`; a provenance pointer would dangle after a deletion; the vector extension is native code that Native AOT must ship. Good as a test double, not as the store.
5. **D. DuckDB.** Excellent for scanning history as of a date. One writer, a larger resident footprint, and AOT support for its .NET binding is UNKNOWN. Worse than PostgreSQL for a store the app writes to on every episode.
6. **E. Rust sidecar** (one static binary: `sqlx`, an MCP crate, Postgres or SQLite). Smallest resident size of the options (a few MB is typical; not measured here). But the database does the work, so the process language does not move latency; it adds a second toolchain to a .NET shop, a process per consumer or a shared one that both must reach, and a second place for auth. A sidecar is justified only for non-.NET callers.
7. **F. A Native AOT .NET MCP binary over the same library** (`facts-mcp`). Same code as A, hosted alone for agents outside the two apps (Claude Code sessions). Defer until A's resident size is measured; then it is a packaging step, not a rewrite.
8. **G. Adopt Graphiti or Hindsight as the store.** Graphiti needs a graph database or a Python service, which does not fit the memory budget (UNVERIFIED which backends the current release supports). Hindsight already runs and feed-eater already writes to it, but it is a separate service whose rows do not point at Nytka segments or feed-eater items, and it has no per-row version history to query by transaction time (the 2026-09-30 audit: observations have a history, raw facts do not). It stays the cross-project recall layer for agents; this library is the application's own source of truth. Whether the library should also export to Hindsight is UNKNOWN and out of scope.

## Decision

Build **A**, in stages with a kill gate, and treat **B0** and **B1** as the baselines it must beat.

- **Stage 0 (this ADR's PR).** Design, spec and a harness that runs against PostgreSQL. No library code.
- **Gate G0 (Yehor's decision, with numbers).** On the private labelled set, A's extraction plus invalidation must beat B0 by at least 10 points on as-of accuracy, and the real data must contain enough superseded facts to tell them apart (at least 25 planted or found pairs; with 60 episodes a 10-point gap is borderline, so the report states a Wilson interval). If not, build only the feed-eater side and fold the rest into B0.
- **Stage 1 onward.** The slices in `docs/specs/2026-10-26-facts-library.md`, at most three open implementation pull requests at a time.

Rust (E) is rejected for the core. F is the planned second host, not a rewrite.

### Packages

- `Switchknife.Facts` (core; AOT, no reflection JSON, Npgsql only): schema scripts embedded for `Switchknife.Migrations`, the write path, retrieval, entity resolution, the extraction contract and prompt kit, and tool *definitions* (name, description, schema as `JsonTypeInfo`, handler delegate).
- `Switchknife.Facts.Testing`: a scripted `IFactExtractor`, a deterministic timeline with an oracle (the one in `bench/`), and an assertion helper. Consumers test against it.

The MCP SDK is not a dependency. A consumer registers the definitions with its own server in about ten lines. This keeps the AOT rule and avoids coupling to an SDK version; whether the SDK supports Native AOT is UNKNOWN.

## Design

The executable schema is `src/Switchknife.Facts/Migrations/0001_facts.sql` (embedded in `Switchknife.Facts`); CI applies it, so it cannot drift from this text.

### Schema

Seven tables in schema `facts`: `entities`, `entity_aliases`, `predicates`, `episodes`, `facts`, `fact_sources`, `fact_embeddings`.

- **Namespace** (`nytka`, `feed-eater`) on every table that has rows of its own. The two consumers run separate databases, so the namespace only guards a shared one and keeps the schema identical in both.
- **`facts`** holds one assertion: `subject_id`, `predicate`, an object (`object_id` or `object_value`), the rendered sentence `text` (at most 300 characters, as in Nytka), `valid` (`tstzrange`; an unbounded end means unknown, not forever), `recorded_at` and `retired_at` (transaction time), `retired_reason` (`superseded`, `retracted`, `source_changed`, `merged`), `superseded_by`, `fingerprint`, `confidence` and the episode it came from.
- **Valid time** is when the fact was true in the world; **transaction time** is when this database believed it. A query is `(subject, predicate, validAt, knownAt)`: rows with `valid @> validAt`, `recorded_at <= knownAt` and `retired_at` null or after `knownAt`.
- **Immutability.** A row is never updated except to set `retired_at`, `retired_reason` and `superseded_by`. Superseding retires the containing row and writes it again with its range cut at the new start, so at every past instant exactly the rows believed then are visible.
- **Cardinality** (`predicates.cardinality`): `one` (a subject has one value at a time: `lives_in`, `works_at`), `many` (values coexist: `likes`, `knows`), `event` (a point in time, never superseded: `met`, `released`). For `one` the library sets `slot = subject/predicate`, and an exclusion constraint (`btree_gist`) makes two live rows with overlapping `valid` for one slot impossible, even for a writer that bypasses the library. Other predicates leave `slot` null.
- **Open facts.** Nytka's facts are mostly sentences, not triples. A fact with no registered predicate uses the predicate `statement` (cardinality `many`), keeps its `text`, and relies on the model's `replaces` for supersession. Typed predicates are an upgrade path per consumer, not a precondition.
- **Provenance.** `episodes` points at the source (`source_kind`, `source_ref`, `content_hash`, `occurred_at`) and records `extractor`, `model` and token counts. The text stays with the consumer: a locator (segment ids, a line range, a character span) and an optional short quote go in `fact_sources`. A fact may have several sources; corroboration raises `confidence`.
- **Trust.** `episodes.trust` is `owner`, `agent` or `third_party` (a stranger's speech, a web page). A `third_party` episode never retires a fact from a higher-trust episode; it may only add. Recalled text is data, never instruction (the audit's note on memory poisoning).
- **Embeddings** are rows in `fact_embeddings` keyed by fact and model, with no dimension on the column, so a model change adds rows and never forces a migration. Whether PostgreSQL accepts the dimensionless `halfvec` column and exact scans over it is UNVERIFIED here; the harness's CI test is the check.
- **Full text** uses the `simple` configuration. Nytka has a Ukrainian search configuration behind a dictionary; using it needs a per-consumer option, UNKNOWN until Slice F3.

### Ingestion pipeline

One episode, one extraction call, one transaction:

1. **Register** the episode by `(namespace, source_kind, source_ref, content_hash)`. The same hash is a no-op; a new hash for the same `source_ref` is a new version (a paid transcription pass replaces the lines).
2. **Gather context** for the extractor, with no model call: entities already linked to the source (Nytka: the conversation's people; the owner is the `self` entity), the newest entities of the namespace, and the live facts of those entities, cut to a budget (Nytka uses 200).
3. **Extract**: the consumer's `IFactExtractor` makes one call. The library defines the output schema (triples or statements, `validFrom`, `validTo`, `replaces`, evidence ids, salience) and ships prompt fragments for the rules that already work in Nytka's prompt (absolute dates, "never guess a date", cite your lines). The call happens outside any database transaction.
4. **Validate**: drop facts citing evidence outside the episode; drop dates before 1900 or beyond the consumer's horizon (Nytka caps at 730 days); drop a `replaces` that is not a live fact of the same subject; cap facts per episode (Nytka: 5 per run).
5. **Resolve entities**, below.
6. **Apply** in one transaction, which may be the caller's (`IngestAsync(connection, transaction, …)`) so a consumer's "events run in the caller's transaction" rule holds: fingerprint match adds a source only; a retracted fingerprint is skipped, so a deleted fact does not come back; cardinality rules decide supersession; `replaces` retires a `many` or open fact; a failed extraction marks the episode `failed` and writes no facts.
7. **Notify** the consumer through `IFactsObserver`, called inside the transaction.

Retries follow the consumer's job runner (Nytka: three attempts, then an hour's wait, three rounds). The library keeps no queue.

### Invalidation rules

| Case | Result |
|---|---|
| New value of a `one` predicate, later `validFrom` than the live one | Live row retired `superseded`, rewritten ending at the new start; new row from there. |
| New value, earlier than the live one (learned late) | New row ends where the next known one starts; nothing retired. |
| Same value again | A source is added; confidence recomputed. |
| `validTo` stated, no successor ("until March") | A closed range, no successor. |
| `many` or `statement` fact | Never superseded by rule; retired only by an accepted `replaces`. |
| `event` | Never retired by rule. |
| Owner deletes a fact | `retracted`; its fingerprint blocks re-extraction. |
| Source text changed | Facts only the old version supports retire as `source_changed`. |
| Entity merge | `merged_into` set; no fact rewritten; readers follow it. |
| Source deleted for privacy | Purge, below. |

**Purge** is the one exception to immutability. Deleting a conversation or item deletes its episode and every fact that only it supports, then rebuilds each affected `one` slot by replaying the surviving base assertions in recorded order (base assertion = the earliest row per fingerprint). The rebuild is also an integrity check: replaying an untouched slot must reproduce the stored rows exactly.

### Entity resolution

Order, cheapest first, blocking before any comparison:

1. `external_ref` (the consumer's own id, e.g. a Nytka person) wins; the library never makes a second row for it.
2. Exact normalized alias.
3. Candidate generation by the phonetic key and consonant skeleton from chronicle's ADR-002, which collapse Єгор, Егор, Yehor and Egor to one key while Київ and Kyiv meet at the skeleton. These keys only propose; they over-merge by design.
4. Embedding similarity confirms; above a high threshold within one kind, merge to the existing entity; between thresholds, the extractor was shown the candidates and names one, or the library creates a new entity.

No second model call decides a merge. A wrong merge poisons facts and a duplicate is cheap to fix, so ties create a new entity. A scheduled proposal job lists likely duplicates (same skeleton, high similarity, shared facts) and applies a merge only on the consumer's approval. A merge is a row in `entities.merged_into`; unmerging restores aliases but cannot split facts written to the survivor after the merge (known limit).

The blocking is a correctness requirement, not an optimization: comparing each new entity with all existing ones fails between 10⁴ and 10⁵ entities (chronicle ADR-002). Thresholds are UNKNOWN until measured on the labelled set.

### Retrieval

- `AsOf(subject?, predicate?, validAt, knownAt = now)`: structured, index lookups.
- `Search(query, validAt, knownAt, k)`: full text and exact vector distance, fused by reciprocal rank, filtered to rows live at the instants asked. Pre-filter by namespace, time and entity first, so the distance scan touches the filtered rows only.
- `History(factOrSlot)`: all versions with both timelines.
- `Explain(factId)`: the fact, its sources (episode, locator, quote), the supersession chain, the extractor and model, and who retired it and why. The consumer supplies an `ISourceResolver` that turns `(source_kind, source_ref, locator)` into a link or a snippet; the library never reads consumer text.
- `Profile(entity, validAt)`: current facts grouped by predicate; the replacement for Nytka's person card.

An approximate index (HNSW with iterative scans) is added only when exact scan p95 exceeds its threshold at a measured size. Chronicle's ADR-001 gives the reason: filtered approximate search loses recall exactly where date filters are used.

### Tool definitions

| Tool | Writes | Notes |
|---|---|---|
| `facts_query` | no | by entity, predicate, text, `as_of`, `known_at` |
| `facts_history` | no | versions of a slot or fact |
| `facts_explain` | no | provenance, supersession chain, extractor |
| `facts_add_episode` | yes (admin) | registers text and enqueues extraction through the consumer's queue; it never calls the model inline |
| `facts_invalidate` | yes (admin) | retract, or end validity at a date; requires a reason |
| `facts_assert` | yes (admin) | a manual fact, with a `manual` episode |

Read and write tools share the consumer's existing token split (Nytka's `AllowRead()` versus `admin`) and its `Switchknife.McpGuard` endpoint.

### Consumer integration

**Nytka.** Namespace `nytka`; entities `external_ref = nytka:people/<id>`, with the wearer as `self`. Episodes are conversations (`source_ref` = conversation id, locators = segment ids, so Nytka's `memory_sources` maps one to one); trust is `owner` for the wearer's lines and `third_party` for other speakers. Stages: **N1** shadow writes (the existing `extract-memories` handler also writes facts; nothing reads them; compare counts and supersessions on the labelled set), **N2** readers (as-of and explain tools; the person card from `Profile`), **N3** `memories` and `person_facts` become views over `facts`, and `expires_at` becomes the upper bound of `valid`. Nytka's invariants hold: facts are written in the caller's transaction, the model call stays outside it, nothing sensitive reaches logs, and a deleted conversation purges its facts. Honest limit: most Nytka facts are sentences, so typed invalidation helps only the part that can be typed (home, work, relationships, health states); the rest gains as-of queries, provenance and the person card, not smarter invalidation.

**feed-eater.** Namespace `feed-eater`; episodes are items the reader model has read at or above its relevance floor, saved items and notes (not every candidate: that is where cost would run away); trust `third_party` for web text, `owner` for notes. Entities are projects, tools, organisations and people named in articles; predicates are typed from the start (`released`, `acquired`, `maintained_by`, `deprecated`), which is where typed cardinality pays. Corroboration across items becomes `confidence`. The weekly summary to Hindsight stays. The workbench's entity timeline reads `Profile` and `History`.

## Cost

- **Model calls.** Nytka: zero added (the extraction call is widened). feed-eater: one call per gated item, on `gpt-6-luna`; a rough upper bound is 12 read items per digest plus saves, so tens of calls a day. Price per token: UNKNOWN in the repositories (the gateway reports cost per call; step 0 reads it from its logs). At an assumed 2k input and 0.5k output tokens per call and 20 calls a day, volume is about 50k input and 10k output tokens a day; the benchmark replaces the assumption with measured tokens.
- **Memory budget.** No new container and no new model service. Targets, to be confirmed by the harness at 50k facts per consumer:

| Item | Budget | Basis |
|---|---|---|
| Library resident memory in the host process | at most 64 MB over the host's baseline | UNKNOWN; harness peak working set |
| Table and indexes, 50k facts, no vectors | at most 150 MB on disk | UNKNOWN; harness relation sizes |
| Vector rows, 50k × 1024 `halfvec` | about 100 MB (2 bytes per dimension) | arithmetic; at 512 dimensions about 50 MB, if the embedding model supports shortened output (UNVERIFIED for the Nytka model) |
| Working set of one exact scan | at most 100 MB | must fit the Nytka database's 384 MB cap beside its own conversation vectors, which a pending migration adds; this is the tightest line |
| Total added, both consumers | at most 150 MB steady state, about 7% of the 2,089 MB now available | sum of the above, host plus cache |

If a measured line exceeds its budget the response is, in order: scope the scan by time and entity before distance, reduce dimensions, then add the approximate index. Not: a bigger container.

## Failure modes

| Failure | Handling |
|---|---|
| Model returns bad JSON or times out | Consumer retries (three attempts); episode `failed`; no partial facts (one transaction). |
| Model invents a `replaces` id or cites lines outside the episode | Dropped at validation. |
| Wrong invalidation (a fact retired that is still true) | Rows are immutable, so `facts_invalidate` with `restore` writes a new row from history; `Explain` shows who retired it. |
| Wrong entity merge | Reversible for facts before the merge; the known limit above. Ties create, never merge. |
| Hostile text in a web page or a stranger's speech | `third_party` trust cannot retire higher-trust facts; recall output is data. |
| Late or out-of-order facts | Handled by the clipping rule; the benchmark's oracle plants them. |
| Concurrent episodes on one slot | Row locks on live rows; the exclusion constraint rejects the loser of a race on an empty slot; one retry. |
| Extension missing or not creatable by the migration user | Migration fails naming the script (`Switchknife.Migrations` already does); whether the `vector` extension is creatable without a superuser is UNKNOWN. |
| Embedding model changes | Rows are keyed by model; queries filter by it; a re-embed job fills the new model before readers switch. |
| Privacy deletion | Purge and slot rebuild; checked by a test that the rebuild equals incremental state. |
| Exact scan evicts hot pages of the host's own tables | Measured by the harness; scope-before-distance mitigation. |
| Prompt or schema change silently shifts extraction | `episodes` records `extractor` and `model`; the labelled-set run is part of the release check. |

## Consequences

- Each consumer gains a `facts` schema in its own database and a second migration run (`Switchknife.Migrations` accepts one assembly per run; the consumer runs its own and then the library's).
- `Switchknife.Facts` is the first package with a database schema of its own; its migrations are forward-only and additive because fact rows are immutable.
- The README rule "`0.x` until two projects use it" applies; Nytka and feed-eater are the two.
- The public repository carries no real conversation or article text. The labelled set stays private, outside the repository; the harness reads its path from an argument.

## Reopen if

- G0 fails: A does not beat B0 by 10 points on as-of accuracy, or the data holds too few superseded facts to tell them apart.
- Exact-scan p95 exceeds its threshold, or the working set exceeds the budget, at a size the consumers reach within a year.
- A consumer needs multi-hop questions the tables answer slowly (then a graph engine is reconsidered, not before).
- Hindsight gains per-row bi-temporal queries and pointers to external rows.
- A non-.NET consumer appears (then F, or E).
