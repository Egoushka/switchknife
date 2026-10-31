# 5. A knowledge kit on Switchknife.Facts: one layer for code, cooking and money ideas

- Status: proposed
- Date: 2026-10-31

## Conclusion

Build `Switchknife.Knowledge`, a library over `Switchknife.Facts` with three layers (source adapters, the facts store, outputs for agents and the approvals shell), but build it in the order the evidence allows, not in the order of the brief.

1. **Money chain first.** Ideas with a value hypothesis and a cheapest test, linked to experiments, results and money. It needs no retrieval quality, it serves the one goal with a number (about 60k UAH a month of extra income), and making money is 1–2% of what reaches the agents today (the workspace audit of 2026-10-10).
2. **Retrieval repaired before it is shared.** The first instance of this idea already exists: the `prior-art` namespace in feed-eater's database. Its recall gate failed: recall@10 0.145, then 0.183 after reseeding everything written, against a threshold of 0.60 and a plain-text baseline of 0.169 (the workspace's prior-art results of 2026-10-29). Spreading a retriever that does not beat `rg` to cooking and code would make three domains of noise. The kit's retrieval slice must beat that baseline on the same gold set first.
3. **The cross-domain map last, behind the strictest stop.** Automatic analogies between a cooking technique and a code pattern are the boldest and least proven part. They ship only as proposals a person confirms, and stop if the confirmation rate says they are noise.

No new store, container or front end: the kit's namespaces live where `prior-art` already lives, the approvals shell renders its items, and the MCP tools are definitions the host registers behind `Switchknife.McpGuard`.

## Context

Agreed on 2026-10-31: one shared knowledge layer for every domain (code, cooking, money ideas). It feeds AI coding with known practices, surfaces new ideas including income ones, and grows a map across domains. Principles: knowledge capture is part of "done"; bold by default with a check and a stop condition; an abstraction needs two real consumers; open by default (MIT, public) unless the data is personal; no Telegram bots.

What exists, with evidence:

| Part | State | Evidence |
|---|---|---|
| `Switchknife.Facts` 0.1.0 | Bi-temporal facts, provenance (`ExplainAsync`), hybrid search (`SearchAsync`, full text plus vectors fused by reciprocal rank), extraction kit (prompt, parser, validator). Not yet: entity resolution beyond exact names (F4), tool definitions (F6). | `src/Switchknife.Facts/README.md`, `docs/specs/2026-10-26-facts-library.md` slice table |
| Facts performance | 50,000 facts: ingest p95 about 14 ms per 5-fact episode, as-of p95 under 0.4 ms, nearest p95 21 to 51 ms at 1024 dimensions, 57 MB table and indexes | `docs/benchmarks/facts-library.md`, the README's "Measured" |
| feed-eater | Feed reading (RSS, Atom, JSON Feed, OPML), SSRF-guarded page fetch, readable text, dedup (canonical URL, title hash), story clustering, taste ranking, MCP tools behind `Switchknife.McpGuard` 0.1.0 | feed-eater `src/FeedEater/{Sources,Fetch,Text,Ingest,Ranking,Mcp}` |
| prior-art base | Namespace `prior-art` of Facts in feed-eater's database: importers for chargehand `result/v1`, audit tables, research notes, a snapshot CSV; tools `prior_art_search`, `prior_art_explain`, `prior_art_ingest`; coverage `hit`/`thin`/`miss`; a stale flag computed on read | feed-eater commit `0bb8403` (not yet merged), `src/FeedEater/PriorArt/*` |
| prior-art gate | recall@10 0.183 lenient (Wilson 0.128 to 0.240), 0.134 strict; B0 (`rg` plus `feed_search`) 0.169 and 0.087; the ceiling (gold names that appear anywhere in the written research) 0.641; hit p95 383 ms end to end | workspace prior-art results, 2026-10-29 rework run |
| Approvals shell | One app for every decision: sources `PUT /items/{id}` with title, summary, evidence, editable body, actions, callback; the server never acts itself, a decision is a signed callback | `Egoushka/approvals` `docs/specs/2026-10-19-approvals-design.md` |
| Workspace rule D-39 | Nothing new adds a store, a front end or a digest | workspace decisions |

Two things in the brief do not survive the evidence as written. "Feed AI coding with known practices" already has a working pipe and a failing retriever: the pipe is not the problem. And "a knowledge map across domains" has no consumer yet that would act on a link; the idea chain gives it one.

## Options

| Option | What | Verdict |
|---|---|---|
| **A. Library kit over Facts, hosted by an existing service, namespaces per domain in one database** | `Switchknife.Knowledge` (+ adapters) in feed-eater's process first; namespaces `prior-art`, `feed`, `kitchen`, `ideas`, `map` in feed-eater's PostgreSQL | **Recommended.** No new store; cross-domain links are possible because every namespace shares one database. |
| A′. Same kit, its own Native AOT host | A small `knowledge-host` process (MCP, ingest jobs, approvals callbacks), same database | Bold variant; the trigger to move to it is below. A packaging step, not a rewrite, because the kit is a library. |
| B0. Do nothing new | Keep prior-art inside feed-eater; recipes and ideas stay in notes and GitHub issues | The baseline every check below compares against. |
| B1. One Hindsight bank per domain | Retain recipes, practices and ideas as memories | Rejected: no per-fact locator, no transaction-time history, no slot supersession (ADR 0001, option G); it would also make two bases that disagree, which the boundary below forbids. |
| C. Facts per consumer database (ADR 0001's default) | Recipes in their own database, ideas in another | Rejected for this kit: a link between a cooking fact and a code fact cannot be a row if they live in different databases. ADR 0001's rule stays right for Nytka, whose facts are personal and never link out. |
| D. A graph database (Neo4j, Memgraph, Kùzu) | Typed links as a native graph | Rejected again: the longest walk the kit needs is idea → experiment → result → money, four hops, which a recursive query answers; a graph engine is a new store (D-39) and a second copy of the facts. Reopen only on the trigger in ADR 0001. |
| E. Rust or Go ingest sidecar | One static binary that clones, parses and posts episodes | Rejected for the core (ADR 0001, option E: the database does the work). One part of it is kept as an option: `ast-grep` (a Rust structural search tool) as the matcher for code practices, below. |

## Decision

Build **A**, staged, with a gate per stage. Each stage names its acceptance check and its stop condition; a failed stop condition ends that stage and is recorded as an ADR amendment, not argued away.

### Packages

| Package | Holds | Rule |
|---|---|---|
| `Switchknife.Knowledge` | Source adapter contract, ingest pipeline (dedup → consumer's extractor → `FactsStore.IngestAsync`), domain schemas (predicates and entity kinds per domain), the map (link predicates across namespaces), the idea schema and its validator, retrieval across namespaces, analytics queries, tool definitions (name, description, `JsonTypeInfo` schema, handler delegate; no MCP SDK, as Facts F6), approvals item builders | AOT, no reflection JSON, never calls a model or the network itself |
| `Switchknife.Knowledge.Sources` | Adapters: git repository at a commit (README, docs, ADRs), Markdown documents, chargehand `result/v1`, recipes (schema.org `Recipe` JSON-LD and Markdown), audit tables and research notes (moved from feed-eater's prior-art importers) | Each adapter is pure: bytes in, `SourceDocument` out |
| `Switchknife.SafeFetch` | SSRF-guarded HTTP fetch and readable text, lifted from feed-eater | Enters because recipes from URLs are its second consumer |
| `Switchknife.Knowledge.Testing` | Scripted extractor and embedder, a fixture database, gold-set runner | Test-support; `IsAotCompatible` false if its dependencies require it |

The kit never calls a model, the same contract as Facts: the host supplies `IFactExtractor` and an embedder. The kit never posts to Telegram or any chat.

### Layer 1: ingest adapters

An adapter turns a source into `SourceDocument(kind, ref, contentHash, occurredAt, trust, text, locatorScheme)`. The pipeline then does, per document, with no model call before step 4:

1. **Key**: canonical URL or `owner/repo@commit:path`; content hash of the normalized text.
2. **Skip unchanged**: the same `(kind, ref, hash)` is a Facts no-op (`Duplicate`). A re-run over an unchanged source makes zero model calls. This is the property that makes "capture is part of done" cheap enough to run on every merge.
3. **Near-duplicate**: a document whose embedding is within the cluster threshold of one already ingested, and whose version numbers agree (feed-eater's `SameStory` rule), is linked, not extracted again.
4. **Extract** with the host's extractor and Facts' prompt kit; validate; ingest in one transaction.

Trust by source, following prior-art: our own ADRs, audits and a worker's reading of code are `agent`; README text, web pages and recipe sites are `third_party` (they can add and corroborate, never retire an `agent` fact); decisions Yehor confirms in the approvals shell are `owner`.

**Capture is part of done.** Sources that change when work finishes are ingested by the host on a schedule it already runs: merged ADRs in `Egoushka/*`, completed chargehand results, merged dossiers. `knowledge doctor` lists sources in the manifest whose newest version is not in the base; a non-empty list fails the check. Public repositories under study are read from a clone pinned to a commit, as chargehand already does, never from a working copy on a laptop.

### What leaves feed-eater and what stays

The test for each part: does a second consumer need it now, and does it carry no feed-eater type?

| feed-eater part | Goes to | Why |
|---|---|---|
| `Text/UrlCanonicalizer`, `Text/TitleHash` | `Switchknife.Knowledge` (dedup keys) | Every URL source needs them; 26 lines each, no dependencies |
| `Fetch/SafeFetcher`, `IpGuard`, `GuardedConnector`, `ReadableText`, `Text/HtmlText` | `Switchknife.SafeFetch` | Recipe pages and docs fetched by URL need the same SSRF guard; a second copy of a security check is the worst kind of duplicate |
| `Ingest/StoryClusterer` (the rule, not the store) | `Switchknife.Knowledge` near-duplicate step, over Facts embeddings | Its `SameStory` version guard is generic; its `ClusterStore` reads feed-eater's `items` table and stays |
| `PriorArt/*` importers, coverage and stale logic | `Switchknife.Knowledge.Sources` and retrieval | They already sit on Facts; prior-art becomes the kit's first namespace |
| `Sources/FeedParser`, `Opml`, `SafeXml`, `FeedPoller` | stay | Only feeds use them; feed items reach the kit through an adapter feed-eater implements |
| `Ranking/*` (taste model, scorer) | stay | Coupled to votes on items. Reopen when recipes need ranking by taste (then the second consumer exists) |
| Digest, Telegram, duels, swipe, Instagram, `Memory/WeeklyRetain` | stay | Feed-specific outputs; the kit has no digest (D-39) and no Telegram |
| `Llm/LiteLlmClient` | stays | The kit never calls a model |

feed-eater is the first consumer: after stage K1 it references the kit, its prior-art tools become the kit's tool definitions, and its behaviour does not change. **Recipes are the second**: the recipe adapter must land without a change to `Switchknife.Knowledge` core. If it needs one, the abstraction was wrong and the change is reviewed as such.

### Layer 2: storage

`Switchknife.Facts`, unchanged in principle: one namespace per domain, one embedding model across all kit namespaces (a link between namespaces compares vectors, so they must come from one model; rows are already keyed by model, so a switch re-embeds before readers move).

| Namespace | Holds | Entity kinds | Visibility |
|---|---|---|---|
| `prior-art` | How tools and projects solve problems | `tool`, `problem`, `approach` | Public facts; open to every agent |
| `feed` | Claims from read and saved articles (ADR 0001, E1) | `project`, `org`, `person`, `tool` | Public facts |
| `practice` | Code practices with their source (a repo, commit, path and lines; an ADR; a doc) | `practice`, `pattern`, `language` | Public facts |
| `kitchen` | Recipes, techniques, ingredients, and what happened when cooked | `recipe`, `technique`, `ingredient` | Private |
| `ideas` | Ideas, experiments, results, money | `idea`, `experiment`, `result` | Private |
| `map` | Typed links across namespaces | `ref` (points at an entity or fact elsewhere) | Follows the stricter of its two ends |

Cross-namespace reads are kit code: retrieval runs `SearchAsync` per namespace in parallel and fuses by reciprocal rank (the same `1 / (60 + rank)` Facts uses inside one namespace). Facts keeps "one store never reads another's facts"; the kit holds several stores.

### The knowledge map

A link is a fact in namespace `map`. Its subject and object are `ref` entities whose `external_ref` is `facts:<namespace>/<entity or fact id>`. That reuses everything Facts already guarantees: a link has a source, a validity range, a transaction time, a trust level, and `ExplainAsync` answers who proposed it and who confirmed it. No schema change.

| Predicate | Cardinality | Example |
|---|---|---|
| `analogous_to` | many | brining (kitchen) ↔ cache warming (practice): both pay a cost ahead of time so the critical step is fast |
| `applies` | many | mise en place (kitchen) → dependency injection (practice) |
| `inspired` | many | any fact → an idea |
| `tested_by` | many | idea → experiment |
| `produced` | event | experiment → result |
| `earned` | event | result → money (amount in UAH, date, source) |
| `contradicts` | many | a practice ↔ a measured result |
| `derived_from` | many | a practice ↔ the prior-art fact it came from |

**Bold choice: links proposed by a model.** For each new fact, the nearest neighbours in the other namespaces above a similarity floor go to the host's model, which names the shared structure in one sentence or answers "none". A proposal is stored as an `agent` fact with that sentence as its text, and sent to the approvals shell. A confirmed link is written again at `owner` trust; a rejected pair is retracted, so its fingerprint blocks the same proposal.

- Acceptance: of the first 50 proposals shown, at least 40% confirmed. Reason: below that, more than half of the shell's taps on this source are wasted, and the shell exists to take routine gates away from a person (D-39). Starting value, to calibrate after the first 50.
- Stop: under 30% confirmed over 50 proposals, or no confirmed link reaches an `inspired` idea in the first 100 confirmed links. Then proposals stop, and links are made by hand from the tools only.

### Ideas, value hypothesis and the money chain

An `idea` entity is valid only with all of these, enforced by an `IdeaValidator` in the kit (the same pattern as `ExtractionValidator`): an idea missing any of them is rejected with a reason code, not stored half-done.

| Predicate | Cardinality | Content |
|---|---|---|
| `value_kind` | one | `people` or `money` |
| `value_hypothesis` | one | Who gains what, by how much, and the evidence it rests on. For `money`: the expected UAH per month, so the board can sum it against the goal |
| `cheapest_test` | one | The action, its cost cap (hours and UAH), the metric, and the pass line that would keep the idea alive. A test without a pass line is rejected |
| `status` | one | `proposed`, `testing`, `validated`, `killed`, `earning` |
| `kill_reason` | one | Required when `status` becomes `killed` |

Sources of ideas: feed-eater's weekly ideas and the idea machine already file GitHub issues. The issue stays the workflow's source of truth; the kit ingests it as a source (`github-issue`, ref `owner/repo#n`, hash of title and body), so an edited issue retires the facts only the old text supported, and the two never disagree. Money facts come from the system of record for income (UNKNOWN which; open question 4), never from typing amounts twice.

Chain query: one recursive query walks `inspired` → `tested_by` → `produced` → `earned` and returns, per idea, the stages reached, days in each, and UAH earned.

### Analytics

Read-only SQL in the kit, exposed as one tool (`knowledge_board`) and one JSON payload for the shell:

| Measure | Definition |
|---|---|
| Coverage per domain | live facts, entities, sources; share embedded; share stale (stale is computed on read, as prior-art does) |
| Question coverage per domain | share of searches answered `hit`, `thin`, `miss` (from a query log of domain, coverage and a hash; question text kept only for public namespaces) |
| Cross-domain links | confirmed links per pair of domains; share of entities with at least one; proposals confirmed, rejected, pending |
| Idea funnel | ideas per status; conversion proposed → testing → validated → earning; median days per stage; ideas whose test is older than its own time box |
| Money | UAH per month earned by kit ideas, and the sum of `value_hypothesis` estimates of ideas in `testing`, against the 60k UAH target |

### Layer 3: outputs

**MCP tools** (definitions in the kit, registered by the host, guarded by `RequireMcpGuard`):

| Tool | Does |
|---|---|
| `knowledge_search` | `question`, optional `domains`, `known_at`, `limit`. Facts with sources, trust, stale flag; entity-level results (tools, practices, techniques) ranked above single facts; `coverage` |
| `knowledge_explain` | Any fact or link: sources with locator, quote, hash, trust; both timelines; successors |
| `knowledge_practices` | For coding agents; below |
| `knowledge_ingest` | Writes one source document (the formats of `prior_art_ingest`, plus ADR, recipe, issue) |
| `knowledge_idea` | Create or move an idea; runs `IdeaValidator` |
| `knowledge_board` | The analytics above |

The employer fence holds: private namespaces (`kitchen`, `ideas`, links touching them) are never served to a session in the employer workspace, and that workspace denies `knowledge_ingest`, as it does `prior_art_research`.

**Approvals shell.** The kit builds items in the shell's contract; the host `PUT`s them and receives decisions on its callback. What the shell would ask for:

| Item id | Kind | Shows (evidence, body) | Editable | Actions |
|---|---|---|---|---|
| `knowledge:link:<fact>` | decision | the two facts with their sources, the model's sentence naming the shared structure | the sentence | Confirm, Reject, Never this pair (`confirm`) |
| `knowledge:idea:<idea>` | decision | hypothesis, value kind and estimate, cheapest test with cost cap and pass line, the facts that inspired it | hypothesis, test, pass line | Start test, Kill, Snooze |
| `knowledge:result:<experiment>` | decision | the test, its pass line, measured metric if the source has one | metric value, UAH, note | Passed, Failed (kills or keeps the idea), Inconclusive |
| `knowledge:stale:<fact>` | decision | a high-use fact past its stale line, its sources | none | Recheck (starts a chargehand job), Retire, Keep |
| `knowledge:cooked:<recipe>` | decision | a recipe cooked (from the host's own trigger), its techniques | rating 1–5, what changed | Record, Skip |

No digest and no push of its own: the shell's batching rules decide when Yehor sees these. A `GET` of the `knowledge_board` payload is the data a later read-only "board" screen in the shell would render; that screen is the shell's decision, not this kit's.

### Code agents and known practices

How an agent writing code gets a practice with its source:

1. **Pull, always available.** `knowledge_practices(task, repo, language, paths)` returns practices ranked at entity level, each with its source (repo, commit, path, lines; or ADR and section; or URL and date), trust, stale flag, and the repos in our own code that already follow it (a `follows` fact from the ADR and code adapters).
2. **Push at planning time, bold.** chargehand's dossier and spec runs call `knowledge_practices` once per plan and cite what they use. Not on every prompt: a hook that adds context to each turn costs tokens on every call and was not asked for.
3. **Structural match, the unconventional option.** A practice may carry an `ast-grep` rule (Rust, tree-sitter based) that detects code that follows or breaks it. Then retrieval for a file under edit runs by code shape, not words. Deferred until stage K5 passes; offered because word-level retrieval is what failed in prior-art.

The agent's citation is the check: a plan or PR that used a practice names its fact id, and `knowledge_explain` resolves it.

### Boundary with Hindsight

One system of record per kind of statement. The other side may hold a pointer, never a copy; nothing syncs either way.

| Statement | Goes to | Why |
|---|---|---|
| What was said, tried, decided or preferred in a conversation or session | Hindsight | Episodic memory of conversations; that is what it extracts and recalls |
| Yehor's preferences and habits ("prefers small PRs") | Hindsight | About the person, not the world; no source document to cite |
| Decisions and their reasons | ADRs and the workspace decision log (system of record); the kit ingests them as sources | A decision is a document with a commit; Hindsight remembers the conversation around it |
| How tools, libraries and projects work; practices; article claims | Facts (`prior-art`, `practice`, `feed`) | About the world, with a locator, a trust level and two timelines |
| Recipes, techniques, and what happened when a recipe was cooked | Facts (`kitchen`) | Each has a source (a page, a recorded cook) and changes over time ("bake at 200 °C" superseded by "at 180 °C") |
| Ideas, tests, results, money | Facts (`ideas`), with the GitHub issue as the workflow record | Needs the chain and history; an issue edit must retire old facts |
| "We looked this up on 10-29" | Hindsight, by the session retain job as usual | Episodic; the fact itself stays in Facts |

Rules that keep them from disagreeing: the kit never imports from Hindsight; feed-eater's weekly retain excludes every kit namespace (it already excludes prior-art); a session that recalls a world fact from Hindsight and finds it in Facts trusts Facts, because Facts carries the source. Check: `knowledge doctor` asks Hindsight a sample of the kit's gold questions on typed predicates (`one` cardinality only, where a disagreement is detectable) and lists answers that contradict the live fact. Target: zero; each hit is fixed at the source of the stray copy, not by editing either base.

## Acceptance checks and stop conditions

Every benchmark runs from `bench/Switchknife.Knowledge.Bench` against PostgreSQL 18 in a container capped like the target database, with the same method as `docs/benchmarks/facts-library.md`: warm-up excluded, p95 over at least 200 runs, numbers from a quiet machine or labelled as an upper bound. Recall reports carry Wilson intervals, as the Facts G0 gate does. Gold sets stay private, outside the repository; the harness reads their path from an argument.

| # | Stage | Check | Threshold and reason | How | Stop condition |
|---|---|---|---|---|---|
| K0 | Retrieval repair (on `prior-art`, before anything is shared) | entity-level recall@10 on the existing 20-decision gold set (175 gold names) | ≥ 0.32 lenient, i.e. B0 0.169 + 0.15, the margin the prior-art spec set. The old 0.60 is dropped: it sits at the 0.641 ceiling of all written research, so no retriever over that corpus can pass it | `bench recall --gold <path> --systems b0,kit` | Under B0 + 0.10 (0.27) after the slice: agent-facing retrieval stops; agents keep `rg` and `feed_search`; the kit continues for ideas and analytics only |
| K0 | Retrieval repair | hit latency | `knowledge_search` p95 ≤ 150 ms in the database across five namespaces at 50,000 facts each; ≤ 1.0 s end to end. Reason: one namespace measured 21–51 ms; five run in parallel, so the slowest of five plus fusion; 1.0 s is the prior-art limit, measured at 383 ms | `bench latency --namespaces 5 --facts 50000` | p95 over 150 ms: add the per-namespace prefilter by entity before distance (ADR 0001's order), then lower `oversample`; never a bigger container |
| K1 | feed-eater on the kit | behaviour unchanged | feed-eater's full test suite and `selftest.sh` pass with no test edited except imports | feed-eater CI | Any behaviour test needs a change: the extraction is wrong, revert it |
| K1 | Ingest throughput, model path excluded | documents per second through dedup and `IngestAsync` | ≥ 50 per second. Reason: 14 ms p95 per 5-fact episode is about 70 per second for one writer; 50 leaves margin | `bench ingest --docs 10000 --scripted-extractor` | Under 50: profile; the model call dominates real ingest anyway, so this gates only regressions |
| K1 | Idempotence | model calls on a second run over unchanged sources | exactly 0 | `bench ingest --twice` counts extractor calls | Any non-zero: a bug, not a threshold |
| K2 | Recipes, the second consumer | core untouched | the recipe adapter's PR changes no file in `src/Switchknife.Knowledge/` | `git diff --stat` in CI on that PR | A core change is needed: review the abstraction before merging, and record why in this ADR |
| K2 | Recipes | recall@5 on a kitchen gold set (20 questions, gold technique or recipe each) | ≥ B0 + 0.15, where B0 is `rg` over the same recipe files. Reason: the same margin as K0; a smaller one is inside the interval at 20 questions | `bench recall --gold <kitchen path>` | Under B0 + 0.10: recipes stay files with `rg`; the namespace keeps only cooked records |
| K3 | Ideas chain | validator | 100% of stored ideas have value kind, hypothesis, cheapest test with cost cap and pass line | `knowledge doctor` | Not a threshold: the validator rejects |
| K3 | Ideas chain | use | at least one idea reaches `validated` or `killed` through a recorded test after the first 10 ideas enter `testing`. Reason: a chain that records no outcome is a to-do list | `knowledge_board` | No outcome after 10 tests: the chain is not used; fold ideas back into issues only |
| K4 | Map | proposal precision | ≥ 40% confirmed of the first 50 (reason above) | approvals decisions on `knowledge:link:*` | Under 30%, or no confirmed link inspires an idea within 100 confirmed links: stop proposing |
| K5 | Practices for code | recall@5 on a code gold set: 20 merged PRs in `Egoushka/*` whose description or ADR names a practice from an outside source, asked from the PR's task text with the practice name masked | ≥ 0.50 and ≥ B0 + 0.15. Reason: 0.50 means the right practice is in the top five for half the tasks, enough to be worth a call per plan; the margin as in K0 | `bench recall --gold <code path> --tool practices` | Under B0 + 0.10: stop the planning-time push; keep the pull tool |
| K5 | Practices for code | used | ≥ 30% of plans that received practices cite one by fact id. Reason: below that the call is spend without effect; starting value to calibrate | chargehand run logs | Under 15% over 30 plans: stop the push |
| All | Memory | added resident memory in the host and database | host ≤ 64 MB over baseline (ADR 0001's library budget); embeddings ≈ 2.1 KB per fact at 1536 dimensions (the Facts README's 1.4 KB at 1024, scaled), so 100,000 facts ≈ 210 MB | `bench` peak working set and relation sizes | Over budget: lower dimensions or prune unembedded counters (prior-art already skips them); never a bigger container without a decision |
| All | Hindsight boundary | contradictions on typed predicates | 0 | `knowledge doctor --hindsight` | Each hit is fixed at the stray copy |

## Stages and work-in-progress

| Stage | Content | Depends on |
|---|---|---|
| K0 | Retrieval repair on `prior-art`: entity-level ranking, per-namespace fan-out and fusion, the bench harness | Facts 0.1.0 |
| K3 | Ideas chain, `IdeaValidator`, GitHub issue adapter, `knowledge_idea`, `knowledge_board`, approvals idea and result items | Facts 0.1.0; independent of K0 |
| K1 | Kit packages, `Switchknife.SafeFetch`, feed-eater moved onto them | K0 passed or stopped |
| K2 | Recipe adapter and `kitchen` namespace | K1 |
| K4 | Map links and proposals | K2 and K3 |
| K5 | Practices for code, planning-time push, then `ast-grep` matchers | K0 passed |

K0 and K3 start together; at most three implementation pull requests open at once, as for Facts. Each stage is a minor version of the kit while `0.x`.

## Failure modes

| Failure | Handling |
|---|---|
| A web page or recipe site carries instructions | `third_party` trust; text served as data; it cannot retire `agent` or `owner` facts |
| A model proposes a link between unrelated things | It is a proposal until confirmed; rejection retracts it and blocks the pair |
| An idea's money estimate is fantasy | The board shows estimates and earned UAH apart; only `earned` counts toward the goal |
| The same fact enters through two sources | Facts corroborates by fingerprint; confidence rises; one row |
| Private kitchen or money facts reach a work session | Namespace visibility in the tool layer, plus the employer workspace's deny list |
| Embedding model changes | Re-embed every kit namespace before readers switch; links compare only same-model vectors |
| Purge of a private source | Facts `PurgeSourceAsync`; `map` links to purged facts are purged with them (a kit test) |

## Consequences

- feed-eater gains a dependency on the kit and loses its prior-art code and its fetch and dedup helpers to shared packages.
- The repository gains three packages and one test-support package, each with a README, tests and an AOT smoke case (the test-support one excepted).
- ADR 0001's "facts in each consumer's own database" holds for Nytka; for the kit, one database holds several namespaces, because links need it.
- Facts F4 (entity resolution) becomes a dependency of K0: tool and practice names must resolve across sources (`gh:owner/name`, a package id, a plain name) for entity-level ranking to work.

## Open questions

1. **Host.** feed-eater hosts the kit first (recommended: no new process). The trigger to move to A′, a Native AOT `knowledge-host` on the same database: a second domain's ingest needs a release cadence feed-eater's deploys block, or the kit's jobs push feed-eater past its memory budget. Is that trigger acceptable, or should A′ start at K1?
2. **D-39.** Do approvals items from the kit, and a read-only board screen later, count as "a front end" under D-39? This ADR assumes no, since the shell already exists and renders any source.
3. **Recipes.** Where do recipes live today, and in what format? UNKNOWN: no recipe repository is in the workspace manifest. The kitchen gold set (K2) cannot be built until this is answered.
4. **Money's system of record.** Which system records income, so `earned` facts are ingested, not typed twice? UNKNOWN.
5. **Gate reset.** K0 drops the prior-art spec's 0.60 threshold for B0 + 0.15, because 0.60 sits at the corpus ceiling. Does Yehor accept the reset?
6. **Name.** `Switchknife.Knowledge`, or `Switchknife.Facts.Kit` to make the dependency visible?
7. **Embedding model.** One model across every kit namespace; feed-eater's is fixed at 1536 dimensions. Keep it, or move every namespace to a shorter one when K0 measures memory?

## Reopen if

- K0's stop condition fires: the kit continues without agent-facing retrieval, and this ADR is amended.
- A non-.NET consumer appears (then a host process with an HTTP API, A′, becomes the only way in).
- Hindsight gains per-row locators and transaction-time history (then B1 is reconsidered for the public namespaces).
- Links need walks longer than the idea chain (then option D is reconsidered, as ADR 0001 says).
