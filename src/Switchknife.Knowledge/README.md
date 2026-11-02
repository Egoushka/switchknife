# Switchknife.Knowledge

A knowledge kit over [`Switchknife.Facts`](../Switchknife.Facts/README.md). Design and reasons: `docs/adr/0005-knowledge-kit.md`. Never calls a model or the network; Native AOT compatible.

Today it holds retrieval (stage K0) and the **ideas chain** (stage K3). More arrives by stage.

## Retrieval (stage K0)

```bash
dotnet add package Switchknife.Knowledge
```

`0.x`: the API may still change between minor versions. Status: stage K0 of the ADR; measured numbers and the pass and stop line are in [`docs/benchmarks/knowledge-k0.md`](https://github.com/Egoushka/switchknife/blob/main/docs/benchmarks/knowledge-k0.md).

```csharp
var search = new KnowledgeSearch(dataSource, ["prior-art", "notes"]);   // namespaces of one Facts database
var hits = await search.SearchAsync("How do we stop many clients retrying a failing service at once?", limit: 10);
foreach (var hit in hits)
    Console.WriteLine($"{hit.Name} ({hit.Kind}, {hit.Namespace}): {hit.Facts[0].Text}");

// With an embedding of the question (yours; the package never calls a model):
var withMeaning = await search.SearchAsync(question, vector, model: "my-embedding-model");
```

## What it does

- **Words that survive a sentence.** Facts' text list is an AND of every word (`plainto_tsquery`), so a full question matches nothing. Here the question's content words are weighted by idf (rare words count more) and joined by OR, with prefix match.
- **Entity-level ranking.** Facts found for a question are fused by reciprocal rank (text and, when given, vector lists), then summed per subject entity. An entity with several matching facts outranks one lucky fact.
- **Per-namespace fan-out.** Each namespace is searched in parallel, on its own idf, and the lists are fused by reciprocal rank, so a small namespace is not drowned by a large one.
- **Bi-temporal.** `knownAt` asks what the database believed at that instant.
- No model, no network, Native AOT compatible. Needs PostgreSQL with the Facts schema.

## Ideas

Capture has no gate: an idea enters as a `seed` with only its text and source. Promotion to `testing` has one: a value kind, a value hypothesis (who, gain, evidence, and for money an expected UAH per month) and a cheapest test (action, hour and UAH caps, metric, pass line).

```csharp
var draft = new IdeaDraft(
    IdeaStatus.Testing,
    ValueKind.Money,
    new ValueHypothesis("freelancers", "save 2 h a week", "3 interviews", ExpectedUahPerMonth: 5000m),
    new CheapestTest("landing page", CapHours: 6m, CapUah: 0m, "signups", "10 in 14 days"));

IReadOnlyList<IdeaRejection> gaps = IdeaValidator.Validate(IdeaStatus.Seed, draft); // empty: may be written
```

A rejection holds a code and a field name, never text from the draft, so it is safe to log. Statuses move `seed → testing → validated → earning`; `killed` can be reached from any status and returns to `seed` or `testing` (through the full gate).

`IdeaStore` writes this to a `FactsStore` in namespace `ideas`:

```csharp
var ideas = new IdeaStore(dataSource);
await ideas.InitializeAsync();                                          // defines the predicates
var id = await ideas.CaptureAsync("Grade ideas", new NewEpisode("note", "n1", hash));   // title + status = seed
var moved = await ideas.MoveAsync(id, draft, new NewEpisode("note", "n2", hash2));      // validated, then written in one episode
// moved.Accepted, or moved.Rejections (codes only, nothing written)
```

Each status change needs an episode with a new content hash. Leaving `killed` retracts the `kill_reason` (a second step after the write); killing again with the same reason text is then blocked by the retraction, so give a new reason.

The `value_hypothesis` and `cheapest_test` fact values are JSON written by `IdeaJson` with a source-generated context.

### GitHub issues as sources

Issues filed by the idea workflows stay the source of truth; the kit ingests them as sources. `GitHubIssueAdapter` reads an `issues` webhook payload or a REST issue (pull requests are skipped) and makes the episode: kind `github-issue`, ref `owner/repo#n`, hash of the normalized title and body. It never calls GitHub; the host fetches.

```csharp
if (GitHubIssueAdapter.FromPayload(json) is { } seed)
{
    var capture = await ideas.CaptureAsync(seed);   // capture.Ingest.Duplicate when nothing changed
}
```

The same issue twice is a duplicate. An edited issue is a new version of the source: its new title is written, and facts only the old text supported are retired with `source_changed`. Capture writes `status = seed` only while the idea is still a seed, so editing the issue never undoes a promotion made through the approvals item.

### Chain and board

The chain links are facts in `ideas`: `inspired` (anything → idea), `tested_by` (idea → experiment), `produced` (experiment → result, with `outcome`, `metric_value`, `note`) and `earned` (result → UAH, an event dated when the money came).

```csharp
var experiment = await ideas.StartExperimentAsync(id, episode);
var result = await ideas.RecordResultAsync(experiment, ResultOutcome.Passed, episode2, metricValue: 14m);
await ideas.RecordEarnedAsync(result, 3000m, on: paidAt, episode3);

IdeaChain chain = await ideas.ChainAsync(id);              // links reached, days in each, UAH earned: one query
IdeaBoard board = await ideas.BoardAsync(targetUahPerMonth: 60000m);   // one round trip
```

The board holds live counts per status, the funnel (ideas that ever reached each status, and conversions), median days per status over stays that ended, tests older than their `time_box_days` and tests with no time box, and money. Days come from the status history, not from the live row: a status change closes the old row, and those closed rows are the stays. Late yield and question coverage are `null` and listed in `Absent` with a reason until their data exists. `IdeaBoardJsonContext` serializes both payloads with nulls written.

### Tools

`KnowledgeTools.All(ideas)` returns the definitions of `knowledge_idea` and `knowledge_board`. Each has a name, a description, an input schema exported from the source-generated `JsonTypeInfo` (`JsonSchemaExporter`) and a handler that takes and returns JSON text. The host registers them behind `RequireMcpGuard`; the kit does no transport.

```csharp
foreach (var tool in KnowledgeTools.All(ideas))
{
    if (tool.Private && session.IsEmployerWorkspace) continue;   // the employer fence: both tools are private
    host.Register(tool.Name, tool.Description, tool.InputSchema, tool.Handler);
}
```

`knowledge_idea` with `action = capture` checks only that text and a source are present. With `action = promote` it runs `IdeaValidator` and returns its codes (`missing`, `not_positive`, `negative`, `transition_not_allowed`) with the field. Bad arguments come back as `invalid` codes, not exceptions. What a call writes carries `EpisodeTrust.Agent` unless the host passes another trust.

### Approvals items

Two items for the approvals shell, built to its contract (source `knowledge`, string body, editable body paths, actions, a callback the host gives):

- `knowledge:idea:<idea>` shows the seed and the drafted value kind, hypothesis and test as editable fields; actions Start test, Keep as seed, Kill (with a confirm).
- `knowledge:result:<experiment>` shows the test and its pass line; metric value, UAH earned and note are editable; actions Passed, Failed, Inconclusive.

```csharp
var item = KnowledgeApprovals.IdeaItem(new IdeaProposal(id, seed, ValueKind.Money, hypothesis, test), new ItemDelivery(callback));
// host: PUT the item; later, on the callback:
var decision = KnowledgeApprovals.ReadDecision(body);
if (decision is null || !ApprovalSignature.Verify(key, decision)) return Forbidden();   // the host also refuses stale `at` and seen decisions
ApprovalReply reply = await KnowledgeApprovals.ApplyAsync(ideas, item, decision);    // { ok, message } for the shell
```

Nothing in a decision is trusted. An unknown action, a missing item id or decision id, a time that does not parse, an edit to a path the item does not mark editable, a number that does not parse or a negative UAH amount is refused with a code and the field (`invalid action`, `negative body.uah`). Start test merges the edits into the body and passes the draft through `IdeaValidator`, whose codes come back with body paths (`missing body.pass_line`). `ParseIdea` and `ParseResult` do the same without writing.

Each decision is its own source (`approvals`, `<item>/<decision>`, trust `Owner`), so the shell's retry of a decision writes nothing twice and never retires what another decision wrote. Passed (or any outcome) with UAH above zero writes an `earned` event dated with the decision's `at`. `ApprovalSignature` computes the contract's HMAC over `decision|id|action|at|h` with the edited fields hashed as `JSON.stringify` writes them, so Cyrillic text verifies the same as on the shell; the key stays with the host.

The estimate in a hypothesis is never income: only `earned` facts count as UAH earned, and the board keeps the two apart.
