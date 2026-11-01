# Knowledge K3, the ideas chain: spec

Design and reasons: `docs/adr/0005-knowledge-kit.md` (sections "Ideas, value hypothesis and the money chain", "Analytics", "Layer 3: outputs"). Status: proposed. Depends on `Switchknife.Facts` 0.1.0 only; independent of K0.

## Goal

An idea enters the `ideas` namespace with no gate. It moves to `testing` only with a value kind, a value hypothesis and a cheapest test whose pass line can end it. The chain from idea to experiment to result to earned UAH is one query. The board shows estimated and earned UAH apart; only earned counts toward the 60k UAH/month target.

## Out of scope

- The `map` namespace and link proposals (K4). Chain links here are plain facts inside `ideas`; K4 may mirror them into `map`.
- Calling a model. The host drafts hypotheses; the kit validates them.
- An income system. `earned` facts are written once by the owner through the result item (ADR decision 4).
- Late yield and question coverage (they need links and the query log from K0/K4). The board reports them as absent, not zero.
- MCP registration, HTTP calls to GitHub or to the approvals shell. The kit builds definitions, items and parses payloads; the host does I/O.

## Where this differs from the ADR, and why

| ADR says | K3 does | Reason |
|---|---|---|
| Chain links `inspired`, `tested_by`, `produced`, `earned` live in `map` | In `ideas`, object entities in the same namespace | `map` exists only from K4; K3 must work on its own. Same predicates and cardinalities, so K4 can mirror them without a rewrite |
| `cheapest_test` is "the action, cost cap, metric, pass line" | A JSON object `{action, cap_hours, cap_uah, metric, pass_line}` as the fact value, source-generated serializer | Fields must be checked one by one; free text cannot be validated |
| `value_hypothesis` is text | JSON `{who, gain, evidence, expected_uah_per_month}` | The board sums the estimate; it cannot parse a sentence |
| Statuses `seed, testing, validated, killed, earning` | Transitions are checked too: `seed→testing\|killed`, `testing→validated\|killed`, `validated→earning\|killed`, `earning→killed`, `killed→seed\|testing` | A seed cannot become `validated` without a recorded test (the K3 "use" check counts outcomes). `killed→seed\|testing` is the ADR's "can come back through a new link" |

## Slices

One task, one pull request, one K3 pull request open at a time. Each slice merges when its checks pass and CI is green.

| Slice | Content |
|---|---|
| S1 | Package `Switchknife.Knowledge` (skeleton, README, tests project, AOT smoke case), idea types, `IdeaValidator`, predicate set, JSON contexts |
| S2 | `IdeaStore` over `FactsStore`: capture a seed, promote, kill, revive; transitions enforced; Postgres tests |
| S3 | GitHub issue adapter: issue payload to episode and seed facts; an edited issue retires the facts only the old text supported |
| S4 | Chain query and `IdeaBoard`: funnel, days per stage, stuck tests, estimated and earned UAH apart |
| S5 | Tool definitions `knowledge_idea` and `knowledge_board` (name, description, `JsonTypeInfo` schema, handler delegate) |
| S6 | Approvals items `knowledge:idea:<idea>` and `knowledge:result:<experiment>`: builders and decision parsing |

## Acceptance checks

Written before the code. IDs are stable; a slice's pull request lists which it turns green. Checks marked DB need Docker (PostgreSQL 18 container); without it, outside CI, they skip like the other Postgres tests here.

| ID | Slice | Check | Passes when |
|---|---|---|---|
| I1 | S1 | Capture has no gate | `IdeaValidator` accepts a `seed` draft with every field empty |
| I2 | S1 | Promotion gate | A draft in `testing`, `validated` or `earning` is rejected, with a reason code per gap, when value kind, hypothesis (who, gain, evidence), or test (action, metric, pass line, positive hours cap, non-negative UAH cap) is missing or blank |
| I3 | S1 | Money needs a number | `value_kind = money` without a positive `expected_uah_per_month` is rejected; `people` needs none |
| I4 | S1 | Kill needs a reason | `killed` without a non-blank reason is rejected; a killed seed with no hypothesis is accepted |
| I5 | S1 | Transitions | Every pair of the table above is accepted and every other pair rejected (exhaustive test over 5×5) |
| I6 | S1 | Codes, not content | A rejection carries a code and a field name, never text from the draft (a test asserts a secret string in the draft does not appear in `ToString()` of the result) |
| I7 | S1 | AOT | `scripts/aot-smoke.sh` runs the new case: validate, serialize and parse both JSON values without reflection |
| I8 | S2 DB | Capture then read | Capture of a text with a source writes `title` and `status = seed`; `AsOf` returns both |
| I9 | S2 DB | Gate at the store | Promote with an incomplete draft writes nothing and returns the rejection codes; a complete one writes the four facts in one episode |
| I10 | S2 DB | One status | After seed → testing → validated, exactly one live `status` fact; history shows the earlier two as superseded |
| I11 | S2 DB | Revive | A killed idea returns to `testing` only through the full gate; its `kill_reason` is retired |
| I12 | S3 | Hash | The hash is stable for the same title and body, and differs when either changes; label and comment changes do not change it |
| I13 | S3 DB | Idempotent | Ingesting the same issue twice writes once (`Duplicate`) |
| I14 | S3 DB | Edit retires | Ingesting an edited issue retires the `title` fact of the old text (`source_changed`) and writes the new one; status, hypothesis and test set by the owner stay live |
| I15 | S3 | No network | The adapter takes a JSON payload and returns an episode and facts; no `HttpClient` in the package |
| I16 | S4 DB | Chain | For one idea with `inspired`, `tested_by`, `produced`, `earned` links, the query returns the stages reached, days in each and UAH earned; an idea with no links returns only its status |
| I17 | S4 DB | Estimate and earned apart | Two ideas in `testing` with estimates 5000 and 8000 and one `earned` of 3000 give estimated 13000, earned 3000 as two separate numbers; no field holds their sum |
| I18 | S4 DB | Funnel | Counts per status and the seed→testing→validated→earning conversion match a fixture; median days per stage match hand-computed values; a test older than its own time box is listed |
| I19 | S4 | Absent measures | Late yield and question coverage are reported as `null` with a reason, not as zero |
| I20 | S5 | Tool shape | `knowledge_idea` and `knowledge_board` have a name, description, a schema built from `JsonTypeInfo`, and a handler; `knowledge_idea` with `action = capture` runs no validator rule beyond a non-empty text and a source; with `action = promote` it returns the validator codes on rejection |
| I21 | S5 | Employer fence | Both tools are marked private (served only outside the employer workspace); a test asserts the flag |
| I22 | S6 | Idea item | The item has id `knowledge:idea:<idea>`, shows the seed, the draft hypothesis and test as editable fields, and offers Start test, Keep as seed, Kill; a decision parses into a promotion draft that `IdeaValidator` then checks |
| I23 | S6 | Result item | The item has id `knowledge:result:<experiment>`, shows the pass line and offers Passed, Failed, Inconclusive; metric value, UAH and note are editable; Passed with UAH produces an `earned` event with amount, date and source |
| I24 | S6 | Never trust the callback | A decision with an unknown action, a negative UAH amount or a missing item id is refused with a code |

## Checks that stay open after K3 and their numbers

Both are from the ADR and are measured by use, not by tests:

- **Use**: at least one idea reaches `validated` or `killed` through a recorded test after the first 10 ideas enter `testing`. Read from `knowledge_board`. Stop condition: none after 10 tests, then the chain is folded back into issues.
- **Gate coverage** (`knowledge doctor`, K1): 100% of ideas in `testing` or later have the four predicates. The store makes a violation impossible to write; the doctor catches rows written around it.

## Stop condition for K3 as a whole

If the use check fails (no outcome after 10 tests), K3 stops and the ADR is amended, as the ADR says. No slice is added to rescue it.

## Value hypothesis and cheapest test for K3 itself

Hypothesis: a gate at promotion, with estimates and earnings shown apart, makes Yehor kill or finish tests he would otherwise leave open, which is the only way ideas become income. Cheapest test: the first 10 ideas that enter `testing` through the kit; measured by the use check above.
