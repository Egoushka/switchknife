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

The estimate in a hypothesis is never income: only `earned` facts count as UAH earned, and the board keeps the two apart.
