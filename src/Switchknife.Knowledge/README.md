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

The `value_hypothesis` and `cheapest_test` fact values are JSON written by `IdeaJson` with a source-generated context.

The estimate in a hypothesis is never income: only `earned` facts count as UAH earned, and the board keeps the two apart.
