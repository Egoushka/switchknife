# Facts library, F2: the rest of the write path, purge, and search fusion

Part of `docs/specs/2026-10-26-facts-library.md` (slice F2; its fusion half moved to the F3 spec). Design and reasons: `docs/adr/0001-facts-library.md`. The typed core is `docs/specs/2026-10-27-facts-core.md`; vectors are `2026-10-27-facts-vectors.md`. Status: this file is committed before the code; each pull request below ticks its own checks.

## Three pull requests, in this order

| PR | Contents | Files it owns |
|---|---|---|
| **W1** write path | `ValidTo` on `one` predicates, entity-valued objects, retraction and restore, trust, changed source | `FactsStore.cs`, `FactsStore.Retraction.cs` (new), `Models.cs`, `Migrations/0001_facts.sql`, tests |
| **W2** purge | Delete a source and everything only it supports; rebuild each affected `one` slot by replay; the same replay as an integrity check | `FactsStore.Purge.cs` (new), tests. Needs W1 merged: the replay must understand W1's rows. |
| **W3** fusion | moved to `2026-10-29-facts-explain-fusion.md` (slice F3) | |

Schema changes edit `0001_facts.sql` in place: no `facts/v` tag exists yet, so no consumer has applied it. The first tag freezes it; later changes are `0002` and up.

## W1: decisions

**`ValidTo` on a `one` predicate.** The new value lands where it begins, as today. It ends at the earliest of its own `ValidTo`, the end of the live row it lands in, and the start of the next known value. If it ends before the containing row does, the rest of the containing row (the tail) is written again with the old value, so the old value resumes. Without a containing row (a gap) there is no tail. A `ValidTo` not after `ValidFrom` is still refused.

**One assertion, several pieces.** Clipping and tails split one assertion into several rows. They share its fingerprint, as the narrowed copy already does. The "one live row per fingerprint" index therefore becomes: `(namespace, fingerprint)` for rows without a slot (`many`, `event`), `(namespace, fingerprint, lower(valid))` for rows with one. The base assertion of a fingerprint is its earliest row, as the ADR's purge says.

**Entity-valued objects.** `NewFact.ObjectEntity` (a `Guid?`) is an alternative to `Value`: exactly one of the two is given, and `Text` is required with an entity (the library does not look the name up on the write path). Rows store `object_id`; `Fact` carries `ObjectEntity`. Supersession, corroboration and fingerprints compare the entity id, never a rendering of it.

**Retraction.** `RetractAsync(factId)` retires every live row of that fact's assertion with reason `retracted` and records the fingerprint in a new table `facts.retractions`. Every write path skips a retracted fingerprint: no row, no source, no supersession of the row it would have landed in. On a `one` slot the retracted interval becomes a gap (unknown), and the row it clipped is not reopened; reopening needs replay and arrives with W2 as an open question below. `RestoreAsync(factId)` lifts the block and writes the assertion's retracted rows again as new live rows (same text, range, sources); it throws `InvalidOperationException` when a newer fact now occupies the interval. Both are idempotent and return the number of rows touched.

**Trust.** `NewEpisode.Trust` is `Owner` (default), `Agent` or `ThirdParty`, stored in the existing `episodes.trust` column. A fact's trust is the highest trust among its sources, so an owner who confirms a stranger's claim lifts it. An episode never retires or clips a live row of higher trust. The new fact is still stored, already retired with the new reason `outranked` (added to the `retired_reason` check), range as it would have been; it shows in `HistoryAsync` and never in `AsOfAsync`. The same value from a lower trust only adds a source. `IngestResult.Outranked` counts them.

**Changed source.** An ingest of a source (`source_kind`, `source_ref`) with a new `content_hash` makes every earlier episode of that source stale. After the new facts are written (so re-asserted facts gain the new episode as a source), live facts whose every source is a stale episode of this source retire with `source_changed`; facts with any other source stay. One statement, once per non-duplicate ingest. Known limit: returning to an older hash is a duplicate episode and does nothing; give the reverted text a new hash.

**Hot path.** No new per-fact round trip. The retracted-fingerprint check rides on the statement that already reads the slot (`one`) or the insert (`many`, `event`); the trust of a live row comes from the same slot query. The episode insert reports how many earlier versions the source has; only when there are some does the ingest add two statements (lock the slots of the facts they support, then retire what only they support), so a first version costs what it did before.

## W1: acceptance checks

xUnit in `tests/Switchknife.Facts.Tests`, Postgres in Docker as the other tests here. A3 to A7, A16, A19, A22, C1, L6, L7 and the vector tests keep passing unchanged.

| ID | Test | Passes when |
|---|---|---|
| A8 | `A_changed_source_retires_facts_it_no_longer_supports` | source S at hash h1 yields `lives_in Kyiv`, `likes tea`, `likes jazz`; at h2 it yields `lives_in Kyiv`, `likes tea`. Then Kyiv and tea are live with two sources each, jazz is retired `source_changed`, and a second ingest of h2 changes nothing |
| A8b | `A_fact_with_another_source_survives_a_changed_source` | `likes jazz` also asserted by source T: after S changes, jazz stays live with T as its only live source |
| A8c | `A_changed_source_leaves_a_gap_it_does_not_invent_a_value` | a `one` value supported only by the old hash retires `source_changed`; as of now nothing answers for the slot |
| A10 | `A_retracted_fact_is_not_extracted_again` | retract a `many` fact and a `one` fact; an episode that yields the same fingerprints writes 0 rows and corroborates 0; the `one` slot's other rows are untouched |
| A10b | `Restoring_a_retracted_fact_makes_it_live_and_extractable_again` | restore brings back text, range and sources; a later extraction corroborates; restore after a newer fact took the interval throws `InvalidOperationException`; retract and restore twice change nothing the second time |
| A12 | `A_third_party_episode_cannot_retire_an_owner_fact` | owner `lives_in Lviv`; a third-party episode says `Kyiv` from a later day: Lviv stays live and open, Kyiv is in `HistoryAsync` as `outranked`, absent from `AsOfAsync` at every `(T, K)`, `Outranked` is 1 |
| A12b | `An_owner_episode_retires_a_third_party_fact` | the reverse order supersedes normally |
| A12c | `A_third_party_fact_the_owner_confirms_gains_owner_trust` | after an owner episode asserts the same value, a further third-party value cannot retire it |
| A23 | `A_closed_range_on_a_one_predicate_keeps_the_tail_of_the_containing_row` | Lviv from day 0, Kyiv from day 10 to day 20: Lviv at day 5 and 25, Kyiv at day 15; History shows the tail sharing Lviv's fingerprint; Kyiv ending after the containing row ends at the row's end; Kyiv in a gap has no tail; Kyiv ending after the next known value ends where it starts |
| A23b | `A_one_fact_that_ends_before_it_begins_is_refused` | `ArgumentException`, nothing written |
| A24 | `Entity_valued_objects_supersede_and_corroborate_by_entity` | `works_at` Acme then Globex supersedes; Acme twice corroborates; both or neither of value and entity, or an entity without `Text`, is refused; `Fact.ObjectEntity` round trips |
| A26 | `Two_live_copies_of_one_piece_cannot_exist_by_raw_sql` | raw insert of a second live row with the same fingerprint and start fails with SQLSTATE `23505`; two live pieces of one fingerprint with different starts are allowed |
| A27 | `The_write_path_adds_no_per_fact_round_trip` | a counting `DbDataSource` wrapper over a 5-fact `one` episode and a 5-fact `many` episode: the statement count of each is at most the pre-W1 count (12 and 6) |
| G1 | the harness gates | `ingestPerEpisode.p95` at most 30 ms at 50,000 facts, `asOf.p95`, size and G4 as before; numbers in the pull request |

Every test is shown to fail: the pull request lists the mutation (a line changed in the code) that turns each red.

## W2: decisions

`PurgeSourceAsync(sourceKind, sourceRef)` deletes every episode of that source (all its versions) and its `fact_sources`, deletes every fact outside a `one` slot whose sources are all among them (with embeddings and vectors), and re-points the `episode_id` of the others to a surviving source. Each `one` slot it touched is then **replayed**: the slot's rows, embeddings and retraction-log rows are read into memory, deleted, and the surviving history is run again through the write path itself (`WriteOneAsync`, the changed-source retirement, retract and restore) with each step's own recorded time as its clock. The steps are the slot's (episode, assertion, locator, quote) ingests ordered by the episode's `recorded_at` then id, each episode's changed-source retirement right after its facts, and the log's retractions and restorations at their own times (an ingest sorts before a retraction at the same instant). An assertion is what its source lines say; an assertion with no surviving source line vanishes. Everything runs in one transaction under the slot locks.

Replay needs what each episode asserted, which the rows did not keep: a source line can land on a row that belongs to a neighbouring assertion of the same value (the same city asserted from two different days), so the line's episode cannot be read off the row it sits on. The schema (still editable: no `facts/v` tag) therefore gains, on `facts.fact_sources`, `assertion` (the assertion's fingerprint), its start, the end it asked for, value or entity, and text, written by the same statement that already writes the source line (no extra round trip); the primary key includes `assertion`. And `facts.retractions` becomes an event log (`restored_at`, primary key `(namespace, fingerprint, retracted_at)`) instead of a set. "Earlier versions of a source" is ordered by `(recorded_at, id)`, not "any other episode", so a replay at an episode's time does not see its own successors. A source line with no `assertion` (a `many` or `event` fact) is not replayed: those slots need no rebuild. The slot locks are now taken before the clock is read, so writers of one slot are recorded in the order they were applied, which the replay relies on.

Rows of a replayed slot get new ids (a row's id cannot be kept while its neighbours are rewritten). An embedding belongs to the assertion's text, so every row of the assertion gets it again, which also repairs the case W1 found: today a narrowed copy has none. `PurgeResult` reports episodes deleted, assertions that no longer exist and slots rebuilt.

`VerifySlotAsync(subject, predicate)` runs the same replay inside a transaction it rolls back and returns the rows (ids ignored; sources, retired reason and successor flag included) that differ from the stored ones: empty is healthy. It writes nothing. This is the integrity check the ADR names.

Known limits: two facts of one episode on one slot that start at the same instant replay in a fixed order, not the original; an ingest and a retraction at the same instant replay as ingest first; a purge rewrites history on purpose, so beliefs "as known at K" before the purge change; `many` and `event` rows need no rebuild.

## W2: acceptance checks

| ID | Test | Passes when |
|---|---|---|
| A13 | `Purging_a_source_rebuilds_the_slot_as_if_it_never_existed` | a store that ingested episodes E1 to E6 (supersessions, a late fact, a closed range, a retraction, a third-party fact) then purged E3 equals a second store that never saw E3: every row of both compared on subject, predicate, value, valid, recorded_at, retired_at, retired_reason and source refs, ids ignored |
| A13b | `Replaying_an_untouched_slot_reproduces_the_stored_rows_exactly` | `VerifySlotAsync` is empty for each of 200 slots of the benchmark workload; after a raw edit of one retired row it names that slot |
| A13c | `Purge_deletes_what_only_the_source_supports_and_keeps_the_rest` | facts supported only by the source and their embeddings and vectors are gone; a fact also supported by another source stays, with its source list shortened and a surviving `episode_id`; the episode row is gone |
| A13d | `Purge_touches_nothing_outside_the_affected_slots` | a hash of every row of unrelated slots is equal before and after, ids included |
| A13g | `Purge_removes_the_retraction_of_an_assertion_that_vanishes` | a fact only the source supported had been retracted: after the purge neither it nor its log row exists; a retraction of a surviving fact stays and still blocks |
| A13e | `Purge_keeps_embeddings_of_rebuilt_rows` | after a rebuild that rewrites a row, `NearestAsync` still returns it for its own vector |
| A13f | `A_failed_purge_changes_nothing` | a fault injected after the deletes (a raw trigger raising on insert) leaves every table as before |
| A14 | `Purge_is_atomic_with_concurrent_writers_on_the_slot` | a purge racing 4 writers on one slot ends with no overlap and the oracle's answers for the surviving episodes |

Yehor reads this diff: it deletes data.

## W3: moved

The fusion decisions and checks (L2, L2b, F3a to F3d, and the ones added since) live in `docs/specs/2026-10-29-facts-explain-fusion.md`, next to Explain, which ships in the same slice (F3). They are not repeated here.

## Not in this plan

Reopening the clipped predecessor on retraction (replay-based; decide after W2: a retraction could call the same rebuild instead of leaving a gap, at the cost of rewriting history), `replaces` validation (A11) and failed-episode status (A9) (F5), entity merge (F4), and the retrieval scorer for the labelled set (after G0).
