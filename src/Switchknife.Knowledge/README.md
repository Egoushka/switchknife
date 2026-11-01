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

The estimate in a hypothesis is never income: only `earned` facts count as UAH earned, and the board keeps the two apart.
