# Knowledge K4, research rounds: spec

Design and reasons: `docs/adr/0005-knowledge-kit.md` (sections "Layer 1: ingest adapters", "Ideas, value hypothesis and the money chain", and the amendment "K4 research rounds"), `docs/adr/0006-k0-stop-on-production-corpus.md`. Status: proposed. Depends on `Switchknife.Facts` 0.1.0, K3 S2 (`IdeaStore`, merged) and K1 S2 (`Keys.UrlCanonicalizer`, not merged). K3 S4, the board, merged in #45, so it no longer blocks the build.

## Goal

Research compounds: each round on a topic starts from what earlier rounds stored, and says how much of what it found was new. A round is an episode with a question and the list of facts it read. Its facts go through a dedupe key, so a repeated finding adds a source to the existing fact and does not create a second row. A finding that contradicts a stored one is recorded as a contradiction and does not sit unnoticed beside it. The round reports **saturation** (new facts ÷ submitted facts). When saturation falls, the topic is researched enough. A killed idea leaves a reason the next round's brief shows, so the same dead idea is not proposed again.

## What already exists, and what K4 adds

| Need | Already there | Evidence | K4 adds |
|---|---|---|---|
| Exact repeat adds a source, not a row | Facts fingerprint `subject\|predicate\|object` (plus start for `event`); `IngestResult.Corroborated` | `FactsStore.Fingerprint`, `Models.cs` `IngestResult` | Normalization before the fingerprint: `Uber` and `uber ` and `Uber.` are one object today only if the writer typed them the same |
| Same source twice writes nothing | Episode `(kind, ref, hash)` is `Duplicate` | `IngestResult.Duplicate` | Nothing |
| A new value replaces the old one | Slot supersession on `one` predicates, history kept | Facts write path spec (A23, A24) | A count of supersessions per round, and a rule for `many` predicates (below) |
| Canonical source URL | `UrlCanonicalizer` | K1 S2, not merged | Used as the source part of the key |
| Kill reason | `kill_reason` (`one`, text) on the idea | `IdeaPredicates.KillReason` | `killed_because` (`many`): the fact ids that killed it |
| Seeds with value hypothesis and cheapest test | `IdeaStore.CaptureAsync`, `MoveAsync`, `IdeaValidator` | K3 S2 | A mapping from the dataset's feature records; `RecordDraftAsync` so a seed can hold its draft |
| Bulk import on the host | `prior-art import <file\|->` (formats `result`, `audit`, `notes`, `csv`); the dataset reaches it today as Markdown (`export_notes.py` → `--format notes`) | feed-eater#50 (read at `870bffc`), `PriorArtCommand` | A JSONL importer (`--format research`) that reads the dataset's records directly, removing the three losses of the notes route (below); `research_brief` |

## Out of scope

- Semantic search for the brief. ADR 0006 stopped agent-facing retrieval (recall@10 0.199 against a gate of 0.32). The brief uses exact filters only: topic, then entity. A semantic brief waits for a new retrieval design with its own gate.
- Calling a model. The host's agent writes the facts and decides what contradicts what; the kit normalizes, counts and stores.
- Changing the Facts schema or its supersession rules.
- The `map` namespace and link proposals (ADR stage K6 after the amendment).
- Changing K3's types, predicates or gates. K4 adds files under `src/Switchknife.Knowledge/Research/`, calls `IdeaStore` through its public methods, and adds one method, `IdeaStore.RecordDraftAsync`, which reuses `IdeaValidator` unchanged.

## Where this differs from the brief, and why

| Brief says | K4 does | Reason |
|---|---|---|
| "A contradiction supersedes it" | On a `one` predicate, yes: Facts supersedes by slot, with history. On a `many` predicate, the new fact is written and a `contradicts` fact links it to the old one; both stay live, and the brief shows the pair | A `many` predicate has no slot, so a different object is simply a second fact. Retiring the old one on the new one's word would let a `third_party` web page retire an `agent` reading of code, which ADR 0005's trust rule forbids. The owner retracts the loser |
| A `killed_because` predicate | `killed_because` (`many`, value `facts:<namespace>/<fact id>`) beside the existing `kill_reason` text; the reason's class (`<class>: …`) goes to `kill_class` (`one`) | `kill_reason` already holds the why as text; a second text field would duplicate it. What is missing is which facts killed the idea, so a later round that contradicts them can reopen it. The dataset owner proposed the class as the `killed_because` value; a class cannot be contradicted by a round, a fact can, so the class gets its own predicate and stays filterable |
| Dedupe key "subject + predicate + object, plus the canonical source URL" | The fact key is normalized subject + predicate + object. The canonical URL keys the **source**: the same finding from a second URL corroborates (one fact, two sources); from the same URL twice it is a no-op | With the URL inside the fact key, the same claim from two pages would be two facts, and corroboration (confidence rising with independent sources) would never happen |
| Build waits for K3 S4 | Waits for K1 S2 only | S4 merged; `UrlCanonicalizer` has not |
| Contradiction needs a field naming the old fact | Detected by key: same subject, predicate and value, different `holds` | The dataset's status rule makes it detectable; a writer-supplied id would be one more thing to get wrong |
| `value_hypothesis.evidence` becomes fact references in K3 | K3 keeps text (resolved from the ids); K4 adds `evidence_fact` references beside it | Changing K3's type changes `ValueHypothesis` JSON, the approvals idea item's editable field and the validator for one consumer. Two-consumer rule: revisit when a second source supplies evidence as ids |
| A feature with hypothesis and test enters as an idea | It enters as a `seed` holding its draft (`RecordDraftAsync`); it is not promoted | The dataset's own rule: `testing` needs a recorded test, which happens after the dataset |

## Contract

**Round.** An episode with source kind `research-round` and ref `<topic>/round-<n>` in namespace `research`. Entity `topic` (name = topic slug), entity `round`. Facts on the round:

| Predicate | Cardinality | Value |
|---|---|---|
| `round_of` | one | the topic entity |
| `asked` | one | the round's question |
| `read` | many | `facts:<namespace>/<fact id>` for each fact the round was shown (from the brief) |
| `saturation` | one | `{submitted, new, corroborated, superseded, contradicted, rejected}` as JSON |
| `contradicts` | many | `facts:<ns>/<new> -> facts:<ns>/<old>`, one per contradiction on a `many` predicate |

The round's findings are written in a target namespace the host names: `research` for phone-shell (private, added by the ADR amendment), `prior-art` for peer gaps. Each finding goes in one episode per source and round (below), so `ExplainAsync` names the round that brought each source.

**Wire format.** The kit reads the phone-shell dataset's own JSONL shapes: `research/phone-shell/schema/fact.schema.json` and `feature.schema.json` (research#21, schema as of commit `cc482a7`, which added the fact `origin`). It defines no line format of its own, so the first consumer's schema is the contract and the other consumers convert to it. Consumer 3 (chargehand claims) checks whether that holds.

**Why a JSONL importer, not the notes route.** Today the dataset reaches feed-eater as Markdown: `scripts/export_notes.py > notes.md`, then `prior-art import notes.md --format notes --ref research/phone-shell/facts.jsonl`, one `###` entry per http(s) source titled `<subject>: <fact id>`. That route loses three things, and each is a K4 input:

| Loss in the notes route | Effect | K4's JSONL importer |
|---|---|---|
| A source that is only a repo file (`<repo>/<path>:<line>`) is dropped: the notes importer needs an http(s) URL | The owner's own probes and results, the `agent`-trust sources, never arrive; a fact with only file sources arrives with none | `sources[].file` becomes a `repo-file` episode (trust `agent`), line range in the locator |
| One URL per entry: a fact with N sources becomes N statements | Corroboration is invisible, and N copies inflate any count | One fact, N episodes; the sources corroborate it (`IngestResult.Corroborated`), and the brief shows N independent sources |
| Status survives only as a text tag | REJECTED and CONFIRMED claims of the same thing look alike to the store; nothing can detect a contradiction | Status is in the fact's value (`holds`), so it is part of the fingerprint and contradictions are found by key |

The notes route stays for Markdown notes that have no schema; the dataset moves to `--format research` when S4 ships.

**Fact record → Facts.**

| Field | Goes to | Rule |
|---|---|---|
| `id` (`FA-<code>-NNN`) | the source line's locator `{"id", "round", "shard"}` | Facts has no external id on facts. Feature references (`evidence`, `needs`) resolve an id through the locator, across rounds. Two ids that land on one fact (corroboration) both resolve to it |
| `subject` | entity, kind `subject`, name `FactKey.Normalize(subject)` | Entity resolution beyond the exact normalized name waits for Facts F4 |
| `predicate` | predicate as is (the pattern already fits Facts) | Defined on first sight, cardinality `many`, unless the host's cardinality map says `one` (see "Open with the dataset owner") |
| `value` + `status` | `Value` = canonical JSON `{"holds":"yes"\|"partial"\|"no","value":<v>}` | `holds` comes from status: CONFIRMED → `yes`, PARTIAL → `partial`, REJECTED → `no`. Status is in the object, so it is part of the fingerprint: a REJECTED claim never corroborates a CONFIRMED one. `<v>` in canonical form: a string goes through `FactKey.Normalize`; a number in invariant round-trip form; a boolean as `true`/`false`; an object or array as JSON with sorted keys and no whitespace; null as `null` |
| `status` UNKNOWN | no fact; an `open_question` fact on the round (`subject \| predicate`) | UNKNOWN asserts nothing, so it must not corroborate or contradict anything. The brief lists an open question until a live fact with that subject and predicate exists |
| `text` | `Text` (first 300 characters; the full text in the locator) | Facts caps `Text` at 300; the schema allows 600 |
| `valid_from` | `ValidFrom` | null → the earliest `accessed` date of its sources, with `"valid_from":"unknown"` in the locator. Facts needs a start; "seen true on that day" is the honest lower bound |
| `sources[].url` | episode kind `web`, trust `third_party`, ref `<canonical URL>@<topic>/round-<n>` | See "Source ref" below |
| `sources[].file` | episode kind `repo-file`, trust `agent`, ref `<repo>/<path>@<topic>/round-<n>`, line range in the locator | The owner's own results and probes are an agent's reading, as with prior-art's `repo-read` |
| `sources[].quote` | `Quote` | — |
| `sources[].accessed` | episode `OccurredAt` and the locator | — |
| `origin` (optional, `["<round>:<shard>:<id>", …]`) | not read from shards (absent there: `merge.py` sets it, never an agent); read from the merged `facts.jsonl` for the cross-check and id resolution below | Provenance from the dataset's own dedupe |

**Source ref carries the round.** Facts treats a new episode of an existing source ref as a new version of that source and retires facts only the old version supported (`source_changed`). Round 2 citing a URL that round 1 cited, for different facts, would therefore retire round 1's facts. The round suffix prevents that. The cost: the same URL in two rounds counts as two sources of one fact. The brief reports **independent sources** as distinct canonical URLs or files with the suffix removed. Saturation is unaffected, because a re-cited fact counts as known either way. The episode content hash is SHA-256 over the sorted fact keys and quotes the episode carries, so re-importing a round is `Duplicate`.

**Which records a round imports.** Facts: the round's shards (`shards/r<n>-*.facts.jsonl`), as written, before the dataset's own dedupe. The kit's key does the dedupe, and the denominator is what the round's agents submitted. The merged `facts.jsonl` cannot replace the shards: it holds the dataset's merge decisions, so new-versus-known under the kit's key could not be counted from it. Features: the merged `features.jsonl` (dedupe merges and passes applied, so scores and kills are current), where the earliest `origin` round is `n`. A feature whose `origin` gains round `n` but started earlier counts as known. Feature novelty is reported beside fact saturation (`features: {submitted, new}`, submitted = feature lines in the round's shards); it is the dataset's own "under 20% new features" stop signal, computed by the kit.

**What the fact `origin` adds.** `merge.py` unions `origin` on dedupe, so the merged file says which shard records each merged fact came from. The importer uses it twice:

1. **Cross-check.** The number of `r<n>:` entries across the merged file's `origin` lists must equal the valid record count of round `n`'s shards. A mismatch (a shard edited after merge, a pass that dropped a fact) is reported as `origin_mismatch` with the ids, and the import still runs from the shards.
2. **Id resolution.** A feature's `evidence`, `needs` and `killed_by` hold merged fact ids. When a merged id is not a shard id of any imported round, the importer resolves it through the merged fact's `origin` to the shard ids it was built from, then through their locators to the kit's fact. An id that resolves to nothing is `rejected` with code `unresolved_fact_id`.

A feature `origin` that names no round (`doc:<repo>/<path>`, `session:<name>`, `yehor`) is imported with the first round of its topic and left out of feature novelty, since no round's agents found it.

**The dataset's dedupe is the gold set for the key.** `dedupe/fact-merges*.jsonl` lists pairs an agent judged to be one fact. The kit reports **key recall**: the share of those pairs that the kit also put on one fact. This is reported, not gated. A low number tells which normalization rule to add next, instead of a guess.

**Fact key.** `FactKey.Normalize`: Unicode NFKC, case fold (invariant), whitespace runs to one space, trim, trailing `.,;:` dropped. Applied to the subject name and string values before the fingerprint; the stored `Text` keeps the writer's wording. Numbers stay numbers (the schema puts units in the predicate), so `1000` and `1000.0` are one value and `"1,000"` as a string is another. That is a starting rule, widened when key recall shows a miss.

**Saturation.** `new ÷ submitted`. `submitted` counts valid fact records of the round's shards after in-batch duplicates are merged, UNKNOWN records excluded (they become open questions). `new` = `Written` facts that did not supersede a live row. Corroborated, outranked and duplicate-episode facts count as known. Supersessions and contradictions are reported separately and are not counted as new: a round that only overturns old facts has still learned something the ratio should not hide. The stop condition reads `new ÷ submitted` alone. `rejected` records (schema violations, an unresolvable id) are not in the denominator.

**Contradiction.** Two records with the same subject, predicate and canonical `<v>` but a different `holds` contradict. On a `one` predicate, Facts supersedes by slot. On a `many` predicate, both stay live and a `contradicts` fact on the round links them. The dataset needs no `contradicts` field, because its "phrase a capability positively; status says whether it holds" rule (`decisions.md`) makes contradictions detectable by key.

**Feature record → idea** (namespace `ideas`, entity `external_ref = research:<topic>/<FE id>`).

| Field | Goes to | Why |
|---|---|---|
| `id` | the idea entity's `external_ref` | Stable across rounds; dataset dedupe already remapped dropped ids |
| `title` | `title` through `IdeaStore.CaptureAsync` | K3 |
| `status` `seed` | stays `seed` | `decisions.md`: `testing` needs a recorded test, which happens after the dataset |
| `status` `killed`, `kill_reason` | `MoveAsync` to `killed` with `kill_reason` (K3) | K3's gate: killed needs a reason; the schema enforces it too |
| `kill_reason` → `killed_because` | `killed_because` (many), value `facts:<ns>/<fact id>` for each `FA-` id in the optional `killed_by`, else each `FA-` id found in `kill_reason` | The facts that killed it, so a round that contradicts them shows the idea in the brief as reopenable. A rule kill ("no messenger bots") has no fact and writes none |
| `kill_reason` class | `kill_class` (one), the text before the first `:` of `kill_reason` (`<class>: <evidence>`), normalized | A filter for the brief and the board ("killed by cost" across topics); a `kill_reason` without the prefix writes none |
| `value_kind`, `value_hypothesis`, `cheapest_test` | the three K3 predicates on the **seed**, through a new `IdeaStore.RecordDraftAsync` (K4 S3) | K3's `MoveAsync` writes them only when leaving `seed`. `RecordDraftAsync` runs `IdeaValidator` as if promoting to `testing` and writes the three facts with status unchanged; a draft that fails writes nothing and returns the codes. A later promotion supersedes them by slot |
| `value_hypothesis.evidence` (fact ids) | K3 `Evidence` text = each id's resolved fact text joined as `FA-…: <text>` lines; **and** `evidence_fact` (many), value `facts:<ns>/<fact id>` | Both: K3's type stays text (no K3 contract change for one consumer), and the link survives for the brief and for K6's map to mirror. Recorded in the "differs" table |
| `needs` | `needs` (many), `facts:<ns>/<fact id>` | A feature becomes reopenable or blocked when a needed fact turns `no` |
| `depends_on` | `depends_on` (many), object entity = the other idea | Same namespace, so a plain entity link; written in a second pass after all seeds exist |
| `area` | `area` (one) | A filter for the brief and the board; Facts has no tags, so a tag is a predicate |
| `layer` | `layer` (one) | Same |
| `wild` | `wild` (one, `0`–`3`) | Same |
| `summary` | `summary` (one) | The idea's body; K3 keeps only the title |
| `aliases` | `alias` (many), normalized | Feature-level dedupe across topics: a seed from another topic whose normalized title or alias matches is reported as a candidate merge, never merged automatically |
| `prior_art` | `prior_art` (many), canonical URL | Links ideas to outside work; the peer-gap topic reads the same URLs |
| `scores` | `scores` (one, JSON `{value, effort, confidence}`); null writes nothing | Kept for priority (`value × confidence ÷ effort`, `decisions.md`); estimates, never earned |
| `origin` | the source line's locator | Provenance, not a claim about the idea |

All K4 predicates in `ideas` (`killed_because`, `kill_class`, `evidence_fact`, `needs`, `depends_on`, `area`, `layer`, `wild`, `summary`, `alias`, `prior_art`, `scores`) are defined by `Research/ResearchPredicates`, not added to K3's `IdeaPredicates`. Cross-namespace references are values (`facts:<ns>/<id>`), not links, until K6's map exists: Facts links only within a namespace.

**Brief** (`ResearchBrief.ForTopicAsync(topic, entities?, area?)`, read-only SQL like `IdeaStore.BoardAsync`): live facts written by any round of the topic, plus live facts whose subject is in `entities` (exact entity name after `FactKey.Normalize`), each with its independent source count; open questions; open contradictions; the topic's ideas with status, `area`, `layer` and `scores`, and the ideas whose `needs` or `evidence_fact` point at a fact now `holds = no`; killed ideas with `kill_reason` and `killed_because`; saturation per earlier round. Ordered by subject, then predicate; capped by count with a `truncated` flag, never by score.

## Host side (feed-eater)

| Change | What |
|---|---|
| `prior-art import --format research <dir> --round <topic>/round-<n> --question <text> [--namespace research]` | The JSONL importer. Reads the round's shards, the merged `facts.jsonl` (for `origin`) and the merged `features.jsonl` from a dataset directory, as above; prints the round's saturation, feature novelty, key recall and any `origin_mismatch` as JSON. Replaces `export_notes.py` + `--format notes` for this dataset. The dataset's `scripts/validate.py` must pass first; the importer refuses a directory it fails on |
| `prior_art_ingest` | Gains `round`, `question` and format `research_round` (content: one round's shards concatenated plus the round's features) for agents that cannot reach the files; same reply |
| Cardinality map | Read from the dataset's optional `schema/predicates.json` (`{"<predicate>": "one"\|"many"}`); absent means every predicate is `many` |
| `research_brief(topic, entities?, limit?)` | New read-only tool returning the kit's brief; marked private like `knowledge_board` |
| Tool descriptions | "Call `research_brief` before searching the web on a topic; pass the fact ids you were shown as `read`" |

feed-eater#51 (a word match alone no longer makes a fact relevant) is not a dependency: the brief does not search.

## Open with the dataset owner

Asked of the dataset's owning session before its pull request (research#21) merges; each is additive and optional, so the spec holds with either answer. The owner's reply of 2026-11-01 added only the fact `origin` (`cc482a7`) and confirmed the K3 names; it did not take up 1 or 2, so the fallbacks apply until it does:

1. An optional `killed_by` (array of fact ids) on features. Without it, `killed_because` comes from `FA-` ids found in `kill_reason` text (the owner's `<class>: <evidence>` format puts them there).
2. An optional `schema/predicates.json` naming `one` predicates (for example a measured value that a later measurement replaces). Without it, every predicate is `many`, and a remeasurement is a second live fact, not a supersession.
3. Whether `passes/*.jsonl` ever change a fact (not a feature). Facts are imported from shards, so a pass that changes a fact after import would not reach the kit. UNKNOWN; the `origin` cross-check now reports such a change as `origin_mismatch`, and if passes do change facts, the importer applies them to shard records before the key.

## Consumers

| # | Consumer | State | What it proves |
|---|---|---|---|
| 1 | Phone-shell dataset, research#21 (branch `phone-shell-dataset`, head `2ff8972`, open) | Schema (`cc482a7`: fact `origin`), validator and merge script pushed; round 1 being relaunched on 2026-11-01; imported today through the notes route, which S4 replaces | Rounds, saturation, feature → seed mapping, key recall against its dedupe |
| 2 | feed-eater weekly ideas and peer gaps (feed-eater#54) | Open, blocked by #53 | A second topic (`peer-gaps/<repo>`); gap rows are already facts in `prior-art` |
| 3 | chargehand `result/v1` claims | Importer exists (K1 S3 moves it) | Validation: a claim with evidence at a commit maps to a fact record (`sources[].file` = `<repo>/<path>:<lines>` at the commit, status from the claim's confidence); if it needs a field the fact schema lacks, the contract was wrong and the schema owner hears it before K4 S2 merges |

## Slices

One task, one pull request. At most three kit implementation pull requests open across the repository.

| Slice | Repository | Content |
|---|---|---|
| S1 | switchknife | This spec and the ADR amendment |
| S2 | switchknife | `Research/FactKey`, fact and feature record types with JSON contexts (source-generated, matching the dataset schemas), `RoundIngest`, the JSONL importer (round episode, normalize, merge in batch, one episode per source and round, saturation, contradictions by key, open questions, key recall, `origin` cross-check and id resolution), `ResearchPredicates`; AOT smoke case |
| S3 | switchknife | `ResearchBrief`; feature → idea mapping over `IdeaStore`; `IdeaStore.RecordDraftAsync` (additive, in the K3 folder, after K3 closed); Postgres tests |
| S4 | feed-eater | Formats, `round`, `research_brief`; phone-shell round 1 imported |

## Acceptance checks

Written before the code. IDs are stable. DB checks need Docker (PostgreSQL 18), as elsewhere here.

| ID | Slice | Check | Passes when |
|---|---|---|---|
| R1 | S2 | Normalize | `FactKey.Normalize` maps `"Uber. "`, `"uber"`, `"ＵＢＥＲ"` to one key; keeps `"1,000"` and `"1000"` apart; never changes stored `Text` |
| R2 | S2 DB | Duplicate adds a source | Two lines with the same normalized key from two canonical URLs: 1 fact, 2 sources in `ExplainAsync`; `new = 1`, `corroborated = 1` |
| R3 | S2 DB | Same URL twice | Re-importing the round's file: 0 rows, 0 episodes written, saturation `new = 0` |
| R4 | S2 DB | Contradiction, `one` | A new `license` value supersedes the old; history keeps the old with its end; `superseded = 1`, `new = 0` |
| R5 | S2 DB | Contradiction, `many` | A CONFIRMED record and a REJECTED one with the same subject, predicate and value: two live facts, one `contradicts` fact, `contradicted = 1`; they never corroborate; a `third_party` record never retires an `agent` fact |
| R6 | S2 | Saturation arithmetic | Fixture of 10 lines (2 in-batch duplicates, 1 rejected, 2 known, 1 supersession): `submitted = 7`, `new = 4`, ratio 0.571; rejected lines are listed with a code |
| R7 | S2 DB | Round episode | The round has `round_of`, `asked`, `read` facts; each finding's `ExplainAsync` locator names the round |
| R8 | S3 DB | Brief is exact | A fact with the topic in its round, one matched by entity name, and one unrelated but textually similar: the brief returns the first two and not the third; no embedding call is made (scripted embedder counts 0) |
| R9 | S3 DB | Kill shows in the brief | A killed feature whose `kill_reason` names `FA-X-001`: the idea is killed through `IdeaStore`, `killed_because` points at that fact, and the next brief lists the idea with both; a rule kill writes no `killed_because` |
| R10 | S3 DB | Feature → seed with draft | A complete feature stays `seed` with `value_kind`, `value_hypothesis` (evidence text resolved from the ids) and `cheapest_test` live, plus one `evidence_fact` per id; an incomplete one stays a bare seed and returns `IdeaValidator` codes; `depends_on` links two seeds; every other field lands on the predicate in the table, `scores = null` writes nothing |
| R14 | S2 | Canonical value | `1000` and `1000.0` give one key; `{"b":1,"a":2}` and `{"a":2,"b":1}` give one key; `"1,000"` and `1000` give two; status is in the key |
| R15 | S2 DB | Unknown is a question | An UNKNOWN record writes no fact and one `open_question`; the brief lists it until a CONFIRMED record with that subject and predicate arrives, then drops it |
| R16 | S2 DB | Rounds do not retire each other | Round 1 and round 2 cite the same URL for different facts: every round-1 fact stays live; the brief shows 1 independent source for a fact both rounds cited |
| R17 | S2 | Shards, not the merged file | A fixture directory with 2 shards of round 2 and a merged file holding round-1 records: `submitted` counts only the round-2 shard records; feature novelty counts features whose earliest `origin` is round 2 |
| R18 | S2 DB | Key recall | With a fixture `fact-merges.jsonl` of 4 pairs, 3 of which normalize to one key, key recall is reported as 0.75 with the missed pair listed |
| R19 | S2 | Dataset fixtures | The two JSON schemas are copied into `tests/` with the dataset commit they came from; every fixture validates against them in a test. The S2 author also runs the dataset's `scripts/validate.py` on the fixtures once and notes the commit in the pull request (the dataset repository is private, so CI cannot fetch it) |
| R20 | S2 DB | Notes-route losses removed | A fact with one URL source and one file source only (`<repo>/<path>:<line>`): 1 fact, 2 episodes (`web` third_party, `repo-file` agent), the file's line range in the locator; a fact with 3 URL sources: 1 fact with 3 sources, `submitted = 1`; a CONFIRMED and a REJECTED record of one claim are two facts with different `holds` |
| R21 | S2 | `origin` cross-check | A merged `facts.jsonl` whose `origin` lists hold 5 `r2:` entries against 6 valid round-2 shard records: `origin_mismatch` lists the missing id, and saturation is still computed from the 6 shard records |
| R22 | S2 DB | Merged id resolves | A feature's `evidence` names a merged id that is no shard id; its `origin` names two shard ids that the kit put on one fact: `evidence_fact` points at that fact; an id with no record and no `origin` is `rejected` with `unresolved_fact_id` |
| R23 | S3 DB | Kill class | `kill_reason` `"cost: FA-X-001 shows …"` writes `kill_class = cost` and `killed_because` → `FA-X-001`; a reason with no `<class>:` prefix writes no `kill_class` |
| R11 | S3 DB | Compounding | Round 2, given round 1's brief ids as `read`, corroborates one round-1 fact: the brief afterwards shows that fact with sources from both rounds |
| R12 | S2, S3 | No model, no network | `src/Switchknife.Knowledge/Research` references no `HttpClient`, embedder or extractor |
| R13 | S4 | Host round trip | `prior-art import --format research <dir> --round phone-shell/round-1` prints saturation, feature novelty and key recall; `research_brief("phone-shell")` returns the imported facts and seeds |

## Check and stop condition

- **Check:** phone-shell round 2 starts from `research_brief`, reports saturation, and at least one of its facts corroborates or contradicts a round-1 fact (read from the round's `saturation` and `contradicts` facts and `ExplainAsync`).
- **Stop:** after 3 rounds on each of 2 topics (phone-shell and one peer-gap topic), saturation is still 0.7 or higher on rounds 2 and 3 of both. Round 1 starts from nothing, so its ratio is near 1 by construction and is not judged. 0.7 is a starting value to calibrate: it means at most 3 in 10 findings were already known even though the round started from a brief, which says the key misses what is stored or the brief is not used. Then rounds stop: research goes back to Markdown notes in `research`, the round code is deleted from the kit, and the ADR amendment records the result. No slice is added to rescue it.

## Value hypothesis and cheapest test for K4 itself

Hypothesis: research that starts from a brief of what is already known, and is told how much of what it found is new, spends fewer searches per new fact and stops when a topic is saturated, so Yehor's research hours go to the topics that still yield. Cheapest test: phone-shell rounds 1 and 2 by hand through S2 and the host import, before S3: if round 2's saturation is not below round 1's, the brief does not help and S3 waits for the stop decision.
