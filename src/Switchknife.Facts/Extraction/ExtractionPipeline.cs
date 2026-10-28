namespace Switchknife.Facts.Extraction;

/// <summary>What an extractor reports about a call, stored on the episode so <c>Explain</c> can say which prompt and model said a fact.</summary>
/// <param name="Name">The extractor: for the kit, <see cref="ExtractionPrompt.ExtractorName"/> (<c>kit/1+&lt;hash&gt;</c>); null lets the pipeline use that.</param>
/// <param name="Model">The model that answered, as the provider names it.</param>
/// <param name="InputTokens">Tokens in, when the provider says.</param>
/// <param name="OutputTokens">Tokens out, when the provider says.</param>
public sealed record ExtractorInfo(string? Name, string? Model, int? InputTokens = null, int? OutputTokens = null);

/// <summary>What an extractor is asked: the prompt and the context it was built from.</summary>
/// <param name="Prompt">The prompt to send.</param>
/// <param name="Context">The context the output will be checked against.</param>
public sealed record ExtractionRequest(ExtractionPrompt Prompt, ExtractionContext Context);

/// <summary>What an extractor returns.</summary>
/// <param name="Text">The model's reply, unparsed.</param>
/// <param name="Info">Which extractor and model produced it.</param>
public sealed record ExtractorResponse(string Text, ExtractorInfo Info);

/// <summary>
/// The consumer's side of the model call: send <see cref="ExtractionPrompt.System"/> and <see cref="ExtractionPrompt.User"/> to some model
/// (asking for <see cref="ExtractionPrompt.Schema"/> when the provider supports it) and return its reply. The library never makes the call
/// itself, and calls this interface without holding a database connection.
/// </summary>
public interface IFactExtractor
{
    /// <summary>Makes the call. Throwing is how a failed call is reported; the episode is then recorded as failed.</summary>
    Task<ExtractorResponse> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken);
}

/// <summary>How an <see cref="FactsStore.ExtractAsync"/> run ended.</summary>
public enum ExtractionStatus
{
    /// <summary>The reply was read, checked and written (possibly with no facts).</summary>
    Ingested,

    /// <summary>The episode was already stored; the extractor was not called.</summary>
    Duplicate,

    /// <summary>The call or its reply failed; the episode is recorded as failed and nothing else was written.</summary>
    Failed,
}

/// <summary>What an ingest of extracted facts did.</summary>
/// <param name="Ingest">The episode and the counts of the write path.</param>
/// <param name="Replaced">Facts retired because a new fact replaced them.</param>
/// <param name="ReplacesSkipped">Accepted <c>replaces</c> that did nothing: the old fact was no longer live, belongs to another subject, outranks the episode, or the new fact was not written.</param>
/// <param name="Unresolved">Facts skipped because the resolver could not place an entity.</param>
public sealed record ExtractedIngestResult(IngestResult Ingest, int Replaced, int ReplacesSkipped, int Unresolved);

/// <summary>The outcome of a pipeline run.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Ingest">What was written, when <paramref name="Status"/> is <see cref="ExtractionStatus.Ingested"/>.</param>
/// <param name="Validation">Accepted and rejected facts, when the reply was read; its rejection codes are what to log.</param>
/// <param name="FailureCode">The code stored on the failed episode.</param>
/// <param name="Error">The exception of a failed call, for the consumer to log; it may contain source text.</param>
public sealed record ExtractionOutcome(
    ExtractionStatus Status, ExtractedIngestResult? Ingest, ValidatedExtraction? Validation, string? FailureCode, Exception? Error);

/// <summary>Failure codes the pipeline stores on a failed episode.</summary>
public static class ExtractionFailures
{
    /// <summary>The extractor threw (timeout, network, provider error).</summary>
    public const string ExtractorError = "extractor_error";

    /// <summary>The reply held no parsable JSON object.</summary>
    public const string BadJson = "bad_json";

    /// <summary>The reply was JSON, but not an <see cref="ExtractedEpisode"/>.</summary>
    public const string BadShape = "bad_shape";
}
