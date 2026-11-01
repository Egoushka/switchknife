# Claims: `claim/v1`

A claim is a statement plus the evidence it rests on, each piece of evidence pinned to the version of the source that was read. This page defines the schema, says what "re-check" means, and shows how four existing record shapes map onto it.

**Result.** One schema validates a real record of each shape (the check passes). The stop rule fired: the four shapes differ in three candidate required fields, more than the two allowed, so the schema is cut to the three fields all four can fill, and the adapters carry the rest. The consequence matters more than the pass: **none of the four shapes can say how to re-check itself.** A claim that can be re-checked exists only after an adapter adds a locator and a method. Details under [Check and stop result](#check-and-stop-result).

- Schema: [`schemas/claim/v1/claim.schema.json`](../schemas/claim/v1/claim.schema.json) (JSON Schema 2020-12).
- Fixtures: [`schemas/claim/v1/fixtures/`](../schemas/claim/v1/fixtures) (`<shape>.native.json` as the producer wrote it, `<shape>.claim.json` as an adapter would emit it) and [`schemas/claim/v1/invalid/`](../schemas/claim/v1/invalid) (twelve records that must fail).
- Check, run in CI by `dotnet test`: `tests/Switchknife.Claims.Tests`.
- No service and no converter code: the `.claim.json` files were converted by hand.

## Required fields

| Field | Meaning |
|---|---|
| `claim_version` | The constant `claim/v1`. |
| `statement` | One plain sentence, 1 to 600 characters (600 is the largest limit among the four shapes, the phone-shell fact text). |
| `evidence` | At least one item. Each item has a `kind` and a `pin`. |
| `evidence[].kind` | What sort of source it is. An open vocabulary (`file`, `url`, `commit`, `observation`, `web-note`, ...): the producer names it, a resolver for that kind reads it. |
| `evidence[].pin` | Which version of the source was read. At least one of `sha256`, `commit`, `observed_at`. |

A pin has three strengths. `sha256` with `commit` says exactly what bytes were read. `sha256` alone says it without saying where to get them. `observed_at` alone says only when: it detects that a source changed only by comparing a quote or a value, never by hash. `scope` says what a `sha256` covers: `slice` (default; the cited line range or fragment, lines joined by LF, no trailing LF) or `source` (the whole document). The `slice` rule is chargehand's, checked by hand for all three pins in the chargehand fixture against `git show 5dc7a15:<path>`.

## Optional fields

| Field | Meaning |
|---|---|
| `id` | The producer's id; unique only within that producer. |
| `evidence[].locator` | Where in the source: `path:line`, `path:start-end`, a URL, or a URL plus `#json-pointer`. Without it the evidence cannot be re-read. |
| `evidence[].quote` | A passage of the source that supports the statement, at most 300 characters (the phone-shell limit). |
| `recheck` | How to verify the claim again (below). |
| `checked_at` | When the claim last held against its evidence by the method in `recheck`; for a claim never re-checked, when the evidence was read. |
| `confidence` | The producer's belief, 0 to 1. Not comparable across producers. |
| `valid_from`, `valid_to` | World time: when the statement began and stopped holding. An absent `valid_to` means unknown, not forever. |
| `x-<name>` | Adapter-carried data that does not fit the core (`x-chargehand`, `x-facts`, ...). A generic consumer never reads it. |

Unknown fields without the `x-` prefix are rejected, so a typo cannot pass as data.

## What "re-check" means

A claim re-checks itself when it carries three things: where to look (`evidence[].locator`), what was there (`evidence[].pin`), and how to compare (`recheck.method`). `checked_at` says when that last succeeded. The methods form a ladder; each rung includes the ones below it:

| `method` | The re-check passes when | The schema also requires |
|---|---|---|
| `resolve` | every locator still resolves at its pin | a `locator` on every evidence item |
| `hash` | and every `sha256` pin still matches the cited bytes | at least one evidence item with a `sha256` pin |
| `quote` | and the quote is still found in them | at least one evidence item with a `quote` |
| `support` | and a judge still finds the statement supported by them | a `locator` on every item |
| `metric` | the locator re-run returns a value within `expect.tolerance` of `expect.value` | `recheck.expect`; a `locator` on every item |
| `manual` | a person looked | nothing |

`expect` is allowed only with `metric`. `tolerance` is relative (0.1 accepts a change of 10%); absent means exact. `max_age` (`PT48H`, `P7D`) says how old `checked_at` may be before a consumer must treat the claim as unchecked. It is a policy of the claim's owner; what a consumer does with a stale claim is the consumer's choice. The personal website hides its figures after 48 hours (`lib/status.ts:41`) and its ADR 0002 rejects showing staleness in public, so `max_age` is data, not a rendering rule.

The outcome vocabulary of a re-check (held, changed, unresolvable) is not part of v1. Nothing re-checks claims yet, and a vocabulary without a consumer would be a guess.

## Adapter table

Each cell says where the claim field comes from in the native record. `native` is a field with the same meaning; `derived` is a rename, a constant or a reformat that needs nothing outside the record; **supplied** is a value the record does not contain and the adapter must add.

| Claim field | chargehand `result/v1` claim | `Switchknife.Facts` fact | K4 / phone-shell `fact.schema.json` | plainsight receipt / `/status.json` number |
|---|---|---|---|---|
| `statement` | native: `claims[].text` (`result.schema.json:25`) | native: `Fact.Text`, at most 300 characters (`Models.cs:31,62-65`) | native: `text`, 10 to 600 characters (`fact.schema.json:30-35`) | derived: `containers = 132`, from the key and the value. The site's label for the figure lives outside the document |
| `evidence[].kind` | native: `evidence[].kind`, an enum of six (`result.schema.json:40`) | native: `FactSource.SourceKind`, consumer-named (`Explanation.cs:5`) | derived: `url` or `file` from which key is present (`fact.schema.json:60`) | derived: constant `observation` |
| `evidence[].locator` | native: `locator`, `path:start-end` (`result.schema.json:41`) | native but opaque: `LocatorJson` plus `SourceRef` (`Explanation.cs:6-8`); the fixture reads `url`, `file`, `line` out of the consumer's JSON | native: `url` or `file` (`fact.schema.json:62-63`) | **supplied**: the document names no source. The fixture uses `/status.json#/containers`, a path with no origin; the origin is the consumer's configuration |
| `evidence[].pin` | native: `sha256` (`result.schema.json:43`); `commit` is optional (`:42`) and absent from the real result, so the adapter took it from the request | native: `FactSource.ContentHash`, a hash of the whole source version, algorithm unstated (`Explanation.cs:7`) | derived: `accessed`, a date, becomes `observed_at` (`fact.schema.json:73`). No hash exists | derived: `generated` becomes `observed_at` (`lib/status.ts:24`). No hash exists |
| `evidence[].quote` | none: chargehand pins a slice by hash instead | native: `FactSource.Quote` (`Explanation.cs:9`) | native: `quote`, at most 300 characters, empty only for UNKNOWN (`fact.schema.json:68-72`) | none |
| `recheck.method` | **supplied**: no re-check field; the resolver runs once, before the result leaves the node (`GitEvidenceResolver.cs:13`) | **supplied** | **supplied** | **supplied**: the plainsight ideas ask for it (below), nothing stores it |
| `recheck.max_age` | **supplied** | **supplied** | **supplied** | derived from a consumer constant: 48 hours (`lib/status.ts:41`) |
| `checked_at` | **supplied**: `result/v1` carries no timestamp | derived: `locator.retrieved`, a field the consumer added to `LocatorJson`, not the kit | derived: `accessed` | derived: `generated` |
| `confidence` | native: `claims[].confidence` (`result.schema.json:27`) | derived: `locator.confidence`, again the consumer's | none | none |
| `valid_from`, `valid_to` | none | native: `ValidFrom`, `ValidTo` (`Models.cs:62-65`) | native: `valid_from`, a date or null (`fact.schema.json:39-43`); no `valid_to` | none |
| `id` | derived: `<task_id>#<claim index>`; claims have no id of their own | native: `Fact.Id` | native: `id` (`fact.schema.json:10-14`) | none |

The K4 spec maps the same phone-shell fields onto Facts one step further (`docs/specs/2026-11-01-knowledge-k4-rounds.md:71-81`, in the open spec PR). The two adapters agree on `text`, `quote`, `valid_from` and `accessed`; they disagree where the spec truncates `text` to 300 characters (`:76`) and the claim keeps up to 600.

### The plainsight receipt

No receipt is stored anywhere yet; plainsight's `ideas.md` (private research repo) describes three. The status number above is the first one written down. Each idea maps onto the schema, with one gap:

- Idea 1, pin links to the commit they showed (`ideas.md:33-39`, ADR 0016 `owner/repo@sha:path#L10-L18`): `evidence.locator` plus `pin.commit`. Fits. A link that must track the latest version stays unpinned, and then it is not a claim; `observed_at` is the weakest pin that is still a pin.
- Idea 2, published numbers that drift (`ideas.md:41-47`): `recheck.method: metric` with `expect`. Fits. "Only facts with a machine-readable source qualify" is the rule that `metric` needs a `locator` the resolver can read.
- Idea 5, a receipt on every sentence (`ideas.md:63-68`): a `receipts.json` sidecar is an array of claims. The sentence-to-claim alignment map and the "written by the owner, no receipt" marker are not fields: an edited sentence is a sentence with no claim.

## What does not fit

Left to `x-` fields, or dropped, with the reason.

- **chargehand.** `support` (a model's judgement, ADR 0036) is a verdict about the evidence, not a property of it; it goes to `x-chargehand`. Claims share evidence through ids (`claims[].evidence` lists ids from the top-level array, `result.schema.json:26`); the claim form copies each item, so sharing is lost. The result's signature covers the whole result, not one claim: an extracted claim is unsigned. Cost, prompt chain and open questions are run data.
- **Facts.** The structured triple (`Subject` as a GUID, `Predicate`, `Value`) and the store's own lifecycle (`RecordedAt`, `RetiredAt`, retraction, supersession, trust) go to `x-facts`. `ContentHash` pins the extractor's input, which in the fixture is a local note, not the page the note cites. The adapter therefore emits two evidence items: the note with `scope: source`, the page with `observed_at`. A hash re-check of the second is impossible.
- **K4 / phone-shell.** The four-state `status` (CONFIRMED, PARTIAL, UNKNOWN, REJECTED), `subject`, `predicate` and `value` go to `x-phone-shell`. An UNKNOWN fact has no quote and, by the K4 spec, asserts nothing (`:75`), so it is not a claim. The only pin is a date. No real record exists to test against: the dataset is private and holds no facts yet, so this fixture was written to the schema from a public README. It proves the mapping, not the data.
- **`/status.json`.** One document holds many numbers under one `generated` time; the adapter splits it into one claim per number (the fixture converts `containers` only). There is no sentence, no source and no pin stronger than a time.

## Check and stop result

**Check.** One JSON Schema validates all four fixtures. Result: **pass**, 4 of 4, and 12 of 12 invalid records are rejected, each for the one reason its file name gives.

**Criterion, fixed before tallying.** Take the candidate required set: every field that a re-checkable claim needs. For each shape, mark each field *native*, *derived* or *supplied* (the adapter table). A field *differs* if at least one shape has to have it **supplied**. Stop if more than two fields differ.

| Candidate required field | chargehand | Facts | K4 | plainsight | Differs |
|---|---|---|---|---|---|
| `statement` | native | native | native | derived | no |
| `evidence[].kind` | native | native | derived | derived | no |
| `evidence[].pin` | native | native | derived | derived | no |
| `evidence[].locator` | native | native | native | supplied | **yes** |
| `recheck.method` | supplied | supplied | supplied | supplied | **yes** |
| `checked_at` | supplied | derived | derived | derived | **yes** |

Three fields differ, so the stop fires. **Action taken:** the schema requires only `statement`, `evidence[].kind` and `evidence[].pin`; `locator`, `recheck` and `checked_at` are optional and the adapters supply them.

What the pass is worth, then: the cut schema accepts every shape because it asks for little. Fields all four share natively, with no derivation: none. With derivation: a statement, a kind and a pin. A claim is **re-checkable** when it also has a `recheck` and a `locator` on every evidence item; the schema enforces this coherence when `recheck` is present, not its presence. In the four fixtures, all four are re-checkable only because the adapter supplied what the producer never wrote.

What this does not show: the conversions are by hand, so "an adapter can do this mechanically" is argued, not run; the K4 record was authored to the schema; and the pins of two shapes are time-only.

## Next

Two consumers would settle whether the optional half should become required: the audit offer (chargehand claims in, a re-check report out) and plainsight receipts. Neither is in this repository; switchknife is a kit, and a package enters it when a second project needs it.
