# Facts extraction scored against a gold set (P03)

Part of the extraction kit (`docs/specs/2026-10-30-facts-extraction-contract.md`). Lives in `Switchknife.Facts.Testing`, namespace `Switchknife.Facts.Testing.Gold`: a scorer and a runner, both pure, no model call, no JSON, Native AOT safe.

Why: an extraction change (prompt, model, validator rule) is judged by a number, not by feel. The first consumer is feed-eater's fact gold set (`bench/FactGold`, its D-113 spec), whose match rule this scorer copies so the numbers stay comparable. The second shape that must fit is the labelled set of `Switchknife.Facts.Bench` (subject, predicate, object triples): it maps to one anchor group per part.

## Contract

- `GoldFact(Kind, Match, Claim, Deadline)`: `Match` is a list of groups; each group is a list of alternatives. `GoldItem(Id, Facts)`: no facts means a negative item.
- `EmittedFact(Subject, Predicate, Text, Value, Object, ValidTo, Salience)` and `ItemResult(Facts, Error)`.
- `GoldScorer.Score(gold, results, minSalience)`:
  - An expected fact matches an emitted fact of the same item when every group has an alternative that is a substring of the normalised subject + text + value + object (lower case, `$` and `,` removed, dashes and apostrophes folded, spaces collapsed). One emitted fact satisfies at most one expected fact, expected facts are taken in order.
  - An item is recalled when at least ceil(n/2) of its n expected facts match.
  - Reported: items recalled, fact recall, strict precision, negatives clean, kind accuracy and deadline accuracy on matched facts, and a row per item with its missed claims and error.
- `GoldRunner.RunAsync(extractor, cases, options, minSalience)`: per case builds the prompt, calls the extractor, parses, validates, maps the valid facts and scores. An extractor exception, unparseable or wrong-shaped reply is recorded as that item's error (it scores as nothing emitted) and the run goes on; cancellation propagates.

## Acceptance checks (machine-run: `dotnet test tests/Switchknife.Facts.Testing.Tests`)

| # | Check | Pass |
|---|---|---|
| 1 | Gold's own claims fed back | every positive item recalled, precision 1.0, negatives clean |
| 2 | Empty results | 0 recalled, precision null, negatives clean |
| 3 | Half rule | 1 of 2 expected matched is recalled; 0 of 2 is not |
| 4 | One emitted fact serves one expected fact | two expected facts with the same anchors and one emitted: 1 matched |
| 5 | Negative item with an emitted fact | not clean |
| 6 | Kind and deadline | wrong kind still matches, lowers kind accuracy; deadline compared on date prefix |
| 7 | Salience floor | facts under `minSalience` are not counted |
| 8 | Runner with a stub extractor | a scripted reply per item gives the expected score; an extractor error and a bad reply are item errors, the others still score |
| 9 | Triple-shaped gold | a bench-style (s, p, o) gold maps to three groups and scores |
