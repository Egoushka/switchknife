# Switchknife.Knowledge

Retrieval over [Switchknife.Facts](https://github.com/Egoushka/switchknife/blob/main/src/Switchknife.Facts/README.md) namespaces. It answers a question written as a sentence with **entities** (a tool, a practice, a technique) and the two facts that put each there, not with a list of single facts. Design and stages: [`docs/adr/0005-knowledge-kit.md`](https://github.com/Egoushka/switchknife/blob/main/docs/adr/0005-knowledge-kit.md).

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
