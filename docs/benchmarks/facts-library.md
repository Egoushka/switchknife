# Facts library: benchmark plan

Why these numbers: `docs/adr/0001-facts-library.md` (decision, budget) and `docs/specs/2026-10-26-facts-library.md` (gates G0 to G7). Nothing below has been measured yet. Every figure in the ADR that says UNKNOWN is filled in by this plan.

## Two questions, kept apart

1. **Mechanics** (latency, memory, size, throughput, correctness of as-of answers): decided on synthetic data, because the mechanics do not depend on what the text says. The harness in `bench/Switchknife.Facts.Bench` does this today.
2. **Extraction** (does a model find the right facts and the right supersessions in our text): decided only on real Nytka and feed-eater text, which cannot enter this public repository. The scorer is in the repository; the data stays in a private folder.

Mixing them hides failures: a fast store full of wrong facts passes the first and fails the second.

## Mechanics: what the harness measures

Run (the database name must contain `bench`; the run replaces the `facts` schema):

```bash
dotnet run -c Release --project bench/Switchknife.Facts.Bench -- run \
  --connection "Host=localhost;Database=facts_bench;Username=postgres;Password=..." \
  --subjects 2000 --facts 50000 --queries 2000 --episode-size 5 --vector-dims 1024 --fresh > run.json
```

| Number | Field | Method |
|---|---|---|
| Ingest latency, database only | `ingestPerEpisode.p50/p95/p99` | stopwatch around one episode transaction of `--episode-size` facts, including superseding and source rows; no model call |
| Ingest throughput | `factsPerSecond` | facts divided by wall time of the whole ingest, single writer |
| As-of query latency | `asOf.p50/p95/p99` | `(subject, predicate, validAt, knownAt)` at random over 750 days of valid time and the matching transaction times |
| As-of correctness | `asOfWrong`, `asOfOverlap` | each answer compared with the generator's own oracle; both must be 0, and the process exits 1 otherwise |
| Full-text latency | `search.p95` | `plainto_tsquery('simple', word)` over live rows, word sampled from live facts |
| Vector latency | `nearest.p95` | exact top 10 by cosine over live facts, no index, random vectors of `--vector-dims` |
| Size | `factsBytes`, `embeddingBytes`, `liveRows`, `retiredRows` | `pg_total_relation_size` (table, indexes, TOAST) |
| Resident memory of the harness | `harnessPeakRssMb` | peak working set of the process; include the host's idle baseline by running with `--facts 1` first |
| Database memory setting | `sharedBuffers` | `current_setting('shared_buffers')` |

The workload plants late-arriving facts (learned up to 40 days after they began, out of order), 20% many-valued facts, and three single-valued predicates with 1 to several versions each. It is deterministic by `--seed`.

**What the harness cannot see.** The PostgreSQL server's own resident memory and the page-cache effect of an exact scan on the host's other tables. Collect both from the container: `docker stats --no-stream` before, during and after a vector query loop, and the cache hit ratio (`pg_stat_database.blks_hit / (blks_hit + blks_read)`) for the scan. Run once on a laptop container with the production memory limit (`--memory 384m`) and the production image (`pgvector/pgvector:0.8.7-pg18-trixie`), because the budget is about that limit.

### Sizes and repeats

| Run | Facts | Purpose |
|---|---|---|
| small | 5,000 | a year of Nytka at the rate seen so far; the realistic case |
| target | 50,000 | the budget in the ADR |
| stress | 200,000 | where exact scan stops being enough; sets the trigger for an approximate index |

Three repeats each, report median and range. Discard the first run (cold cache) and say so. Hardware: UNKNOWN until run; record CPU, container limit and PostgreSQL version in the report.

### Thresholds

The targets in the spec (G1 to G4) are guesses taken from chronicle's measurements and arithmetic, not from this design. The first baseline replaces them. A threshold is changed by an ADR line with the measured value beside it.

## Extraction: the labelled set

**Size and mix.** 60 episodes: 30 Nytka conversations (or windows of them, at most 4,000 characters) and 30 feed-eater items. Chosen to include at least 25 *supersession pairs*: two statements about one subject and predicate at different times, where the later replaces the earlier. Where real text has too few, plant them by editing a copy of a real episode (change a city, an employer, a status) and mark the copy `planted` so results can be split.

**Why 60.** Enough to see a 20-point difference, not a 10-point one: with 60 episodes and about 150 gold facts the 95% Wilson interval on a proportion near 0.7 is roughly plus or minus 7 points per system, so a paired comparison of two systems resolves gaps of about 10 points only when the systems disagree on enough items. State the interval; do not claim a win inside it. If the interval overlaps, the gate G0 outcome is "undecided", and the next step is more labels, not a decision.

**Where it lives.** Outside every repository, in a folder only Yehor reads, not under any git remote. It never enters this repository, an issue, a log or a prompt that leaves the machine except to the model being tested. Nytka text is the owner's own speech plus strangers' speech; treat the stranger's lines as especially private and prefer episodes where the facts are about the owner.

**How to draw it.** The owner reads candidates from the Nytka server's own tools (conversation list and transcript) and feed-eater's library, picks episodes, and writes the gold file by hand. A model may pre-fill a draft; a human corrects it, and the file says which facts were edited.

**File format.** JSON Lines, one episode per line. `s` is the subject as written, `p` the predicate (use `statement` for an untyped fact), `o` the object, `from` the day it began as `YYYY-MM-DD` or omitted.

```json
{"episode":"n-014","facts":[{"s":"owner","p":"lives_in","o":"Kyiv","from":"2026-03-01"},{"s":"Anna","p":"works_at","o":"a bank"}]}
```

A second file with the same shape holds a system's predictions. Score one against the other:

```bash
dotnet run -c Release --project bench/Switchknife.Facts.Bench -- score --gold gold.jsonl --pred predicted-A.jsonl
```

The output has `truePositives`, `falsePositives`, `falseNegatives`, `precision`, `recall`, `f1` and `dated` (the share of matched facts whose start day also matches). Matching is exact on subject, predicate and object after trimming and ignoring case, so entity spelling mismatches count as errors; that is deliberate, because entity resolution is part of what is tested. The scorer does not forgive paraphrase; if the predictions are free text, normalise them to triples first with the same prompt for every system.

**Systems compared on the same 60 episodes.**

| System | What it is | Calls per episode |
|---|---|---|
| B0 | Nytka's current extraction (memories with `replaces` and `expires`), mapped to triples | 1 |
| B1 | Raw episodes: retrieve by date and embedding, a model answers "what is true now / on date D" | 0 at write, 1 per question |
| A | The design's extraction contract (triples, dates, `replaces`) plus the write path of `Switchknife.Facts.FactsStore` | 1 |

**As-of questions.** For each supersession pair, ask "what was true about S's P on date D" for one date before the change and one after, and "what did we believe on date K" for K between the two episodes' recording times. Gold answers come from the labels; a system answers from its own store (A, B0) or its own reasoning (B1). Report accuracy per system, split by `planted` and real, with intervals. That is the G0 number.

**Model and cost.** The same model (`gpt-6-luna`, through the box gateway) and the same sampling settings for every system; record the model name and the prompt hash in each prediction file. Read tokens in and out and the cost of each call from the gateway's per-call cost header or its log; report cost per 100 episodes (G7). Price per token is UNKNOWN in the repositories, so do not compute cost from a guessed price.

**Repeatability.** Models are not deterministic. Run each system three times, report the mean and the spread, and compare means.

## Entity resolution

Separate small test, synthetic: the five variant groups and nine distinct names in chronicle's `tests/test_resolve.py` re-created as new synthetic names (do not copy real contact names). Measure candidate-generation recall (does the true match appear among candidates) and the false merge rate at the proposed thresholds, with the blocking keys alone, with embeddings added, and with both. A false merge costs more than a missed one, so report both rates, not an F-score.

## Memory budget check

After the 50,000-fact run, fill this table in the report:

| Line | Budget | Measured |
|---|---|---|
| Harness peak RSS over idle | at most 64 MB | |
| Facts table and indexes | at most 150 MB | |
| Embedding rows at 1,024 dimensions | about 100 MB | |
| Database container RSS during a vector query loop (384 MB limit) | under the limit, no restarts | |
| Cache hit ratio of the host's own queries during the loop | not lower than before by more than 2 points | |
| Total added, both consumers | at most 150 MB | |

A line over budget triggers the ordered response in the ADR (scope before distance, fewer dimensions, approximate index), then a re-run. The report is committed as `docs/benchmarks/results/<date>-facts-library.md` with the JSON files beside it.

## Order of work

1. Count what exists: Nytka facts and conversations, feed-eater items read per day, supersessions already visible. (Fills the ADR's volume UNKNOWN.)
2. Run the mechanics at 5,000, 50,000 and 200,000 facts.
3. Draw the labelled set; run B0, B1 and A, three times each.
4. Write the report; Yehor decides gate G0.
