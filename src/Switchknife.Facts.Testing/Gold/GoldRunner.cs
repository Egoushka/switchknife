using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Testing.Gold;

/// <summary>Runs an extractor over a gold set through the kit (prompt, parser, validator) and scores what comes out.</summary>
public static class GoldRunner
{
    /// <summary>
    /// Asks <paramref name="extractor"/> about each case in order and scores the validated facts. A throwing extractor, an unparseable
    /// reply or one of the wrong shape becomes that item's error and counts as nothing emitted; the run goes on. Cancellation is not swallowed.
    /// </summary>
    public static async Task<GoldScore> RunAsync(
        IFactExtractor extractor,
        IEnumerable<GoldCase> cases,
        ExtractionOptions? options = null,
        double minSalience = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(cases);

        var items = new List<GoldItem>();
        var results = new Dictionary<string, ItemResult>(StringComparer.Ordinal);
        foreach (var gold in cases)
        {
            items.Add(gold.Item);
            results[gold.Item.Id] = await RunOneAsync(extractor, gold, options, cancellationToken).ConfigureAwait(false);
        }

        return GoldScorer.Score(items, results, minSalience);
    }

    private static async Task<ItemResult> RunOneAsync(IFactExtractor extractor, GoldCase gold, ExtractionOptions? options, CancellationToken cancellationToken)
    {
        ExtractorResponse response;
        try
        {
            var prompt = ExtractionPrompt.Build(gold.Context, options);
            response = await extractor.ExtractAsync(new ExtractionRequest(prompt, gold.Context), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ItemResult([], ExtractionFailures.ExtractorError);
        }

        if (!ExtractionParser.TryParse(response.Text, out var episode, out var failure))
        {
            return new ItemResult([], failure == ParseFailure.BadShape ? ExtractionFailures.BadShape : ExtractionFailures.BadJson);
        }

        var valid = ExtractionValidator.Validate(episode, gold.Context, options);
        return new ItemResult(
            [.. valid.Facts.Select(f => new EmittedFact(f.Subject.Name, f.Predicate, f.Text, f.Value, f.ObjectEntity?.Name, f.ValidTo, f.Salience))]);
    }
}
