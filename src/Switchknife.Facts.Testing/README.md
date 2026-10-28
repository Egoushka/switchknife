# Switchknife.Facts.Testing

Test doubles for [Switchknife.Facts](../Switchknife.Facts), so code that extracts facts from conversations can be tested without calling a model. No dependency beyond `Switchknife.Facts` and none on a test framework. Native AOT compatible.

```csharp
var extractor = new ScriptedExtractor(
    """{"facts":[{"subject":{"ref":"e1","name":null,"kind":null},"predicate":"likes","value":"jazz","object":null,"text":"Anna likes jazz.","validFrom":"2026-05-01","validTo":null,"replaces":null,"evidence":["l1"],"quote":"like jazz","salience":0.8}]}""",
    new TimeoutException("the model did not answer"));

var first = await store.ExtractAsync(extractor, episode, context);   // the reply goes through the full pipeline
Assert.Equal(ExtractionStatus.Ingested, first.Status);

var second = await store.ExtractAsync(extractor, otherEpisode, context);   // the error becomes a failed episode
Assert.Equal(2, extractor.Calls);
Assert.Contains("[l1] Anna:", extractor.Requests[0].Prompt.User);
```

- `new ScriptedExtractor(step, step, ...)`: the n-th call runs the n-th step; when the steps run out, the last one keeps answering. A `string` is a model reply (reported as model `scripted`), an `ExtractorResponse` is returned as given (to set the extractor name, model and token counts), an `Exception` is thrown.
- `new ScriptedExtractor(async token => response)`: answers every call; wait on the token to simulate a hanging model that only a timeout or cancellation ends.
- `Calls` and `Requests`: how many calls and each `ExtractionRequest` (prompt and context) in arrival order, to assert on what the pipeline sent. Recording is thread-safe.
