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
| Seeds with value hypothesis and cheapest test | `IdeaStore.CaptureAsync`, `MoveAsync`, `IdeaValidator` | K3 S2 | A mapping from `features.jsonl` lines |
| Bulk import on the host | `prior-art import <file\|->` (formats `result`, `audit`, `notes`, `csv`) | feed-eater#50, `PriorArtCommand` | A `facts-jsonl` format, the `round` field, `research_brief` |

## Out of scope

- Semantic search for the brief. ADR 0006 stopped agent-facing retrieval (recall@10 0.199 against a gate of 0.32). The brief uses exact filters only: topic, then entity. A semantic brief waits for a new retrieval design with its own gate.
- Calling a model. The host's agent writes the facts and decides what contradicts what; the kit normalizes, counts and stores.
- Changing the Facts schema or its supersession rules.
- The `map` namespace and link proposals (ADR stage K6 after the amendment).
- K3 code (S3-S6). K4 adds files under `src/Switchknife.Knowledge/Research/` and calls `IdeaStore` through its public methods.

## Where this differs from the brief, and why

| Brief says | K4 does | Reason |
|---|---|---|
| "A contradiction supersedes it" | On a `one` predicate, yes: Facts supersedes by slot, with history. On a `many` predicate, the new fact is written and a `contradicts` fact links it to the old one; both stay live, and the brief shows the pair | A `many` predicate has no slot, so a different object is simply a second fact. Retiring the old one on the new one's word would let a `third_party` web page retire an `agent` reading of code, which ADR 0005's trust rule forbids. The owner retracts the loser |
| A `killed_because` predicate | `killed_because` (`many`, value `facts:<namespace>/<fact id>`) beside the existing `kill_reason` text | `kill_reason` already holds the why as text; a second text field would duplicate it. What is missing is which facts killed the idea, so a later round that contradicts them can reopen it |
| Dedupe key "subject + predicate + object, plus the canonical source URL" | The fact key is normalized subject + predicate + object. The canonical URL keys the **source**: the same finding from a second URL corroborates (one fact, two sources); from the same URL twice it is a no-op | With the URL inside the fact key, the same claim from two pages would be two facts, and corroboration (confidence rising with independent sources) would never happen |
| Build waits for K3 S4 | Waits for K1 S2 only | S4 merged; `UrlCanonicalizer` has not |

## Contract

**Round.** An episode with source kind `research-round` and ref `<topic>/round-<n>` in namespace `research`. Entity `topic` (name = topic slug), entity `round`. Facts on the round:

| Predicate | Cardinality | Value |
|---|---|---|
| `round_of` | one | the topic entity |
| `asked` | one | the round's question |
| `read` | many | `facts:<namespace>/<fact id>` for each fact the round was shown (from the brief) |
| `saturation` | one | `{submitted, new, corroborated, superseded, contradicted, rejected}` as JSON |
| `contradicts` | many | `facts:<ns>/<new> -> facts:<ns>/<old>`, one per contradiction on a `many` predicate |

The round's findings are written in their own namespace (`prior-art` for phone-shell), each as one episode per canonical source URL, with source ref = that URL and the round id in the locator (`{"round":"<topic>/round-<n>"}`), so `ExplainAsync` names the round that brought each source.

**Fact line** (`research-fact/v1`, one JSON object per line): `subject`, `subject_kind`, `predicate`, `object`, `source_url`, optional `quote`, `valid_from`, `confidence`, `contradicts` (a fact id the writer believes this replaces).

**Fact key.** `FactKey.Normalize`: Unicode NFKC, case fold (invariant), whitespace runs to one space, trim, trailing `.,;:` dropped. Applied to subject name and object text before `EnsureEntityAsync` and `IngestAsync`; the stored `Text` keeps the writer's wording. Numbers and dates are not reinterpreted (`1,000` and `1000` stay different) — a starting rule, widened only when a round shows a miss.

**Saturation.** `new ÷ submitted`, where `submitted` counts valid lines after in-batch duplicates are merged, and `new` = `Written` facts that did not supersede a live row. Corroborated, outranked and duplicate-episode facts count as known. Supersessions and contradictions are reported apart, not counted as new, because a round that only overturns old facts has still learned something the ratio should not hide; the stop condition reads `new ÷ submitted` alone. `rejected` lines (missing field, unknown predicate) are not in the denominator.

**Feature line → seed** (`features.jsonl`, K3 field names): `title` → `CaptureAsync`; when `value_kind`, `value_hypothesis` and `cheapest_test` are all present, a `MoveAsync` to `testing` runs, and a rejection by `IdeaValidator` leaves the seed and reports the codes. A line with `status = killed` and `kill_reason` captures then kills, with `killed_because` from its `killed_by` fact ids. The source of each seed is the round episode; `inspired` links the round's facts named in `based_on` to the idea.

**Brief** (`ResearchBrief.ForTopicAsync(topic, entities?)`, read-only SQL like `IdeaStore.BoardAsync`): live facts written by any round of the topic, plus live facts whose subject is in `entities` (exact entity name after `FactKey.Normalize`); open contradictions; ideas inspired by the topic's facts with their status; killed ideas with `kill_reason` and `killed_because`; saturation per earlier round. Ordered by subject, then predicate; capped by count with a `truncated` flag, never by score.

## Host side (feed-eater)

| Change | What |
|---|---|
| `prior_art_ingest` and `prior-art import` | Format `facts_jsonl` (`research-fact/v1` lines) and `features_jsonl`; field `round` (`<topic>/round-<n>`), required for both; field `question`, required on a round's first call. Reply adds the round's saturation JSON |
| `research_brief(topic, entities?, limit?)` | New read-only tool returning the kit's brief; marked private like `knowledge_board` |
| Tool descriptions | "Call `research_brief` before searching the web on a topic; pass the fact ids you were shown as `read`" |

feed-eater#51 (a word match alone no longer makes a fact relevant) is not a dependency: the brief does not search.

## Consumers

| # | Consumer | State | What it proves |
|---|---|---|---|
| 1 | Phone-shell dataset `research/phone-shell/facts.jsonl`, `features.jsonl` | UNKNOWN: on no branch of `research` on 2026-11-01; must be pushed before slice S4 | Rounds, saturation, feature → seed mapping |
| 2 | feed-eater weekly ideas and peer gaps (feed-eater#54) | Open, blocked by #53 | A second topic (`peer-gaps/<repo>`); gap rows are already facts in `prior-art` |
| 3 | chargehand `result/v1` claims | Importer exists (K1 S3 moves it) | Validation: a claim with evidence at a commit is a fact line with a `gh:` source; if it needs a change to `research-fact/v1`, the contract was wrong |

## Slices

One task, one pull request. At most three kit implementation pull requests open across the repository.

| Slice | Repository | Content |
|---|---|---|
| S1 | switchknife | This spec and the ADR amendment |
| S2 | switchknife | `Research/FactKey`, `research-fact/v1` and feature line types with JSON contexts, `RoundIngest` (round episode, normalize, merge in batch, ingest per source, count saturation, `contradicts`), `killed_because` in the predicate set; AOT smoke case |
| S3 | switchknife | `ResearchBrief`, feature → seed mapping over `IdeaStore`; Postgres tests |
| S4 | feed-eater | Formats, `round`, `research_brief`; phone-shell round 1 imported |

## Acceptance checks

Written before the code. IDs are stable. DB checks need Docker (PostgreSQL 18), as elsewhere here.

| ID | Slice | Check | Passes when |
|---|---|---|---|
| R1 | S2 | Normalize | `FactKey.Normalize` maps `"Uber. "`, `"uber"`, `"ＵＢＥＲ"` to one key; keeps `"1,000"` and `"1000"` apart; never changes stored `Text` |
| R2 | S2 DB | Duplicate adds a source | Two lines with the same normalized key from two canonical URLs: 1 fact, 2 sources in `ExplainAsync`; `new = 1`, `corroborated = 1` |
| R3 | S2 DB | Same URL twice | Re-importing the round's file: 0 rows, 0 episodes written, saturation `new = 0` |
| R4 | S2 DB | Contradiction, `one` | A new `license` value supersedes the old; history keeps the old with its end; `superseded = 1`, `new = 0` |
| R5 | S2 DB | Contradiction, `many` | A line with `contradicts` on a `many` predicate writes the new fact and one `contradicts` fact; the old fact stays live; a `third_party` line never retires an `agent` fact |
| R6 | S2 | Saturation arithmetic | Fixture of 10 lines (2 in-batch duplicates, 1 rejected, 2 known, 1 supersession): `submitted = 7`, `new = 4`, ratio 0.571; rejected lines are listed with a code |
| R7 | S2 DB | Round episode | The round has `round_of`, `asked`, `read` facts; each finding's `ExplainAsync` locator names the round |
| R8 | S3 DB | Brief is exact | A fact with the topic in its round, one matched by entity name, and one unrelated but textually similar: the brief returns the first two and not the third; no embedding call is made (scripted embedder counts 0) |
| R9 | S3 DB | Kill shows in the brief | A feature line with `status = killed`, `kill_reason`, `killed_by`: the idea is killed through `IdeaStore`, and the next brief lists it with both reasons |
| R10 | S3 DB | Feature → seed | A complete line reaches `testing`; an incomplete one stays `seed` and returns `IdeaValidator` codes; `based_on` writes `inspired` links |
| R11 | S3 DB | Compounding | Round 2, given round 1's brief ids as `read`, corroborates one round-1 fact: the brief afterwards shows that fact with sources from both rounds |
| R12 | S2, S3 | No model, no network | `src/Switchknife.Knowledge/Research` references no `HttpClient`, embedder or extractor |
| R13 | S4 | Host round trip | `prior-art import --format facts_jsonl --round phone-shell/round-1` prints saturation; `research_brief("phone-shell")` returns the imported facts |

## Check and stop condition

- **Check:** phone-shell round 2 starts from `research_brief`, reports saturation, and at least one of its facts corroborates or contradicts a round-1 fact (read from the round's `saturation` and `contradicts` facts and `ExplainAsync`).
- **Stop:** after 3 rounds on each of 2 topics (phone-shell and one peer-gap topic), saturation is still 0.7 or higher on rounds 2 and 3 of both. Round 1 starts from nothing, so its ratio is near 1 by construction and is not judged. 0.7 is a starting value to calibrate: it means at most 3 in 10 findings were already known even though the round started from a brief, which says the key misses what is stored or the brief is not used. Then rounds stop: research goes back to Markdown notes in `research`, the round code is deleted from the kit, and the ADR amendment records the result. No slice is added to rescue it.

## Value hypothesis and cheapest test for K4 itself

Hypothesis: research that starts from a brief of what is already known, and is told how much of what it found is new, spends fewer searches per new fact and stops when a topic is saturated, so Yehor's research hours go to the topics that still yield. Cheapest test: phone-shell rounds 1 and 2 by hand through S2 and the host import, before S3: if round 2's saturation is not below round 1's, the brief does not help and S3 waits for the stop decision.
