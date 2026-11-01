# Knowledge K1, kit packages and feed-eater on them: spec

Design and reasons: `docs/adr/0005-knowledge-kit.md` (sections "Packages", "Layer 1: ingest adapters", "What leaves feed-eater and what stays", the K1 rows of "Acceptance checks and stop conditions"). Status: proposed. Depends on K0 having passed or stopped: it stopped, and the owner accepted the stop on 2026-11-01 (ADR "K0 status").

## Goal

The parts of feed-eater that a second consumer needs leave it for shared packages, and feed-eater runs on them with its behaviour unchanged. After K1, recipes fetched by URL (K2b) and practices read from repositories (K2) have an SSRF-guarded fetch, dedup keys, an idempotent ingest pipeline and pure source adapters to build on, without a copy of any of them.

## How K1 differs because K0 stopped

K0's stop line fired: the kit's retriever scored recall@10 0.234 at best (768 dimensions; 0.196 at 1536) against a stop line of 0.27, and p95 844 ms against 150 ms. The ADR's rule for that case is "agent-facing retrieval stops; agents keep `rg` and `feed_search`; the kit continues for ideas and analytics only". K1 follows it:

| ADR, written before K0 | K1 after the stop | Reason |
|---|---|---|
| prior-art's "importers, coverage and stale logic" move to `Switchknife.Knowledge.Sources` and retrieval | Only the importers move (ingest side). `PriorArtSearch`, coverage and the stale flag stay in feed-eater, unchanged | Moving them into the kit would make a shared retriever out of the part that did not beat B0 by the margin. The importers are retrieval-free: bytes in, documents out |
| feed-eater's prior-art tools "become the kit's tool definitions" | `prior_art_search`, `prior_art_explain`, `prior_art_ingest` stay feed-eater's own tools, on their current search path. The kit defines no `knowledge_search` tool in K1 | The tool would serve the failed retriever to agents by default |
| The K0 retriever (`KnowledgeSearch`) is the kit's retrieval | It stays in the package as experimental: no tool, no host and no default path calls it; its README section says so | Kept so a later experiment (entity prefilter, F4 entity resolution, the prod corpus) can rerun the bench without rebuilding it; not deleted, not promoted |
| Near-duplicate step over Facts embeddings | Kept, off unless the host passes an embedder; feed-eater's `ClusterStore` uses the moved rule with its own vectors, as today | Near-duplicate is a pairwise threshold on one story, not ranked recall over names, so K0's result does not judge it. It stays opt-in so no host depends on vectors to ingest |
| K1 gates: behaviour unchanged, ingest ≥ 50 docs/s, zero model calls on a re-run | Same three gates. The K0 latency gate is not K1's | Ingest is what the kit is kept for after the stop (capture for ideas, analytics, kitchen) |

Consequence outside K1, recorded so K2b starts right: kitchen search begins on text (the K2b fallback), and the kit retriever is tried there only through K2b's own gate.

## Where this differs from the ADR otherwise, and why

| ADR says | K1 does | Reason |
|---|---|---|
| `Fetch/SafeFetcher` moves to `Switchknife.SafeFetch` | The library type is `GuardedFetcher`. feed-eater keeps a thin `SafeFetcher` that maps its options to `SafeFetchOptions` and its `CursorStore` day counter to `IFetchQuota` | feed-eater's tests construct `SafeFetcher` with `IOptions<FeedEaterOptions>` and `CursorStore`; a library cannot take feed-eater types, and the K1 gate allows only import edits in tests. A different name also avoids an ambiguous `SafeFetcher` in files that import both namespaces |
| The daily fetch cap is part of the fetcher | `IFetchQuota` (optional; none means no cap) | The cap is persisted in the host's database; the library has no store |
| `UrlCanonicalizer`, `TitleHash` go to `Switchknife.Knowledge` | Same, namespace `Switchknife.Knowledge.Keys` | — |

## Out of scope

- Practice adapters (K2), kitchen and recipes (K2b): they build on K1.
- Calling a model or the network from `Switchknife.Knowledge` or `.Sources`. `Switchknife.SafeFetch` is the only package here that opens a connection.
- Feeds: `FeedParser`, `Opml`, `SafeXml`, `FeedPoller`, ranking, digest, Telegram stay in feed-eater (ADR table).
- Any change to production. feed-eater's deploy is a separate pull request to the homelab GitOps repository after its own pull request merges, by its owner.

## Slices

One task, one pull request. At most three kit implementation pull requests open across this repository at once (K3 runs in parallel).

| Slice | Repository | Content |
|---|---|---|
| S1 | switchknife | This spec. `Switchknife.SafeFetch`: `IpGuard`, `GuardedConnector`, `GuardedFetcher` (pages and conditional feed fetch), `IFetchQuota`, `HtmlText`, `ReadableText`; tests ported from feed-eater; README; AOT smoke case |
| S2 | switchknife | `Switchknife.Knowledge` ingest: `SourceDocument`, dedup keys (`Keys.UrlCanonicalizer`, `Keys.TitleHash`), the near-duplicate rule (`SameStory`), `KnowledgeIngest` (key → skip unchanged → optional near-duplicate → extract → `IngestAsync`); `Switchknife.Knowledge.Testing` (scripted embedder, fixture); `bench ingest` |
| S3 | switchknife | `Switchknife.Knowledge.Sources`: prior-art importers (audit tables, research notes, snapshot CSV, chargehand `result/v1`) as pure adapters |
| R | switchknife | Release tags `safe-fetch/v0.1.0`, `knowledge/v0.1.0`, `knowledge-sources/v0.1.0`, `knowledge-testing/v0.1.0` and CHANGELOG entries. Publishing to nuget.org is the owner's call |
| S4 | feed-eater | Reference the released packages; delete the moved code; `SafeFetcher` becomes the adapter above; prior-art importers call the adapters; search, coverage and stale untouched |

## Acceptance checks

Written before the code. IDs are stable; a slice's pull request lists which it turns green. Checks marked DB need Docker (PostgreSQL 18 container); without it, outside CI, they skip like the other Postgres tests here.

| ID | Slice | Check | Passes when |
|---|---|---|---|
| F1 | S1 | Address rules | `IpGuard` accepts global unicast and refuses every range in feed-eater's `IpGuardTests` (private, loopback, CGNAT, link-local, documentation, benchmarking, multicast, IPv4 embedded in mapped, compatible, NAT64 and 6to4 IPv6, Teredo, ULA, link-local v6); the same inline data as feed-eater's test |
| F2 | S1 | One lookup, pinned connect | `GuardedConnector` resolves once, refuses the whole connect if any answer is private, connects only to an address it checked, refuses ports other than 80 and 443 unless the host is trusted |
| F3 | S1 | Fetch rules | `GuardedFetcher`: scheme, port and credentials refused; blocked host and its subdomains `Blocked`; private address literal `Refused`; at most 3 redirects; https → http redirect refused; each redirect target checked again; content type other than HTML or plain text `BadType`; body over the cap cut (page) or `TooLarge` (feed) |
| F4 | S1 | Politeness and memory | One request per second per host, with the next slot reserved so concurrent callers queue (fake time); a failed page host answers `Cached` for 24 h; a feed failure never parks the host; expired entries pruned above the threshold |
| F5 | S1 | Quota | With an `IFetchQuota` that refuses, a page fetch returns `CapReached` and sends nothing; feeds never take quota; no quota means no cap |
| F6 | S1 | Conditional feed | `FetchFeedAsync` sends `If-None-Match` and `If-Modified-Since` from the conditions, returns `NotModified` on 304 and the new validators on 200 |
| F7 | S1 | Timeout | A hanging upstream returns `Failed` after the configured timeout; the caller's own cancellation throws instead |
| F8 | S1 | Readable text | `ReadableText` prefers `article`, then `main`, then `body`, drops page furniture, keeps the title; `HtmlText` flattens blocks to lines; hostile markup that would time a regex out yields empty text, not an exception |
| F9 | S1 | AOT | `scripts/aot-smoke.sh` runs the new case: guard, readable text, and a fetch through a stub handler, without reflection |
| F10 | S1 | No feed-eater types | `src/Switchknife.SafeFetch` has no reference to feed-eater names (`grep -ri feedeater src/Switchknife.SafeFetch` is empty) |
| K1 | S2 | Keys | `UrlCanonicalizer` and `TitleHash` pass feed-eater's existing cases unchanged; document key is canonical URL or `owner/repo@commit:path`; content hash is of the normalized text |
| K2 | S2 DB | Idempotence (ADR gate) | `bench ingest --twice` over unchanged sources: second run makes exactly 0 extractor calls and writes 0 episodes |
| K3 | S2 DB | Throughput (ADR gate) | `bench ingest --docs 10000 --scripted-extractor`: at least 50 documents per second through dedup and `IngestAsync`, model path excluded |
| K4 | S2 | Near-duplicate | The `SameStory` rule matches feed-eater's `StoryClusterer` cases (version guard: "v1.2" and "v1.3" never cluster); without an embedder the step is skipped and every document is extracted |
| K5 | S2 | No model, no network | `Switchknife.Knowledge` references no `HttpClient` and no extractor implementation; the host passes `IFactExtractor` |
| K6 | S2, S3 | Retrieval stays off | No tool definition, host sample or default path in `src/` calls `KnowledgeSearch`; a test asserts the tool list has no `knowledge_search` |
| K7 | S3 | Adapters are pure | Each prior-art adapter turns bytes into `SourceDocument`s with no I/O; feed-eater's importer test fixtures produce the same episodes (kind, ref, hash, text) through the adapter as through the old importer |
| K8 | S4 | Behaviour unchanged (ADR gate) | feed-eater's full test suite and `selftest.sh` pass; `git diff` of `tests/` on that pull request shows only `using` lines changed |
| K9 | S4 | Search untouched | That pull request changes no line of `PriorArtSearch`, coverage or stale code, and adds no reference to `KnowledgeSearch` |

## Stop condition for K1 as a whole

K8 fails, a behaviour test needs more than an import change: the extraction is wrong; revert it in feed-eater and fix the package, as the ADR says. K2 or K3 falling under their limits is a regression to profile, not a reason to stop.

## Value hypothesis and cheapest test for K1 itself

Hypothesis: one SSRF guard and one ingest pipeline, shared, make the recipes path (K2b) and the practices path (K2) a matter of adapters, with no second copy of a security check. Cheapest test: K2's own gate, "the practice adapters change no file in `src/Switchknife.Knowledge/`"; if K2 needs a core change, the K1 shape was wrong.
