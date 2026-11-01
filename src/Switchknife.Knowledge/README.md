# Switchknife.Knowledge

A knowledge kit over [`Switchknife.Facts`](../Switchknife.Facts/README.md). Design and reasons: `docs/adr/0005-knowledge-kit.md`. Never calls a model or the network; Native AOT compatible.

Today it holds the **ideas chain** (stage K3). More arrives by stage.

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

The estimate in a hypothesis is never income: only `earned` facts count as UAH earned, and the board keeps the two apart.
