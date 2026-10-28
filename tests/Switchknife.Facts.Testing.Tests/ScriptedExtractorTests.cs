using Switchknife.Facts.Extraction;

namespace Switchknife.Facts.Testing.Tests;

public sealed class ScriptedExtractorTests
{
    private static readonly ExtractionContext Context = new(
        new DateTimeOffset(2026, 5, 9, 0, 0, 0, TimeSpan.Zero),
        [new EvidenceLine("l1", "I like jazz.", "Anna")],
        [new PredicateSpec("likes", Cardinality.Many)],
        []);

    private static Task<ExtractorResponse> Call(ScriptedExtractor extractor, CancellationToken cancellationToken = default) =>
        extractor.ExtractAsync(new ExtractionRequest(ExtractionPrompt.Build(Context), Context), cancellationToken);

    [Fact]
    public async Task Replies_come_in_order_and_the_last_one_repeats()
    {
        var extractor = new ScriptedExtractor("first", "second");

        var texts = new[] { (await Call(extractor)).Text, (await Call(extractor)).Text, (await Call(extractor)).Text };

        Assert.Equal(["first", "second", "second"], texts);
        Assert.Equal(3, extractor.Calls);
    }

    [Fact]
    public async Task A_string_step_reports_the_scripted_model_and_a_response_step_is_returned_as_given()
    {
        var given = new ExtractorResponse("x", new ExtractorInfo("my-extractor", "gpt-test", 120, 30));
        var extractor = new ScriptedExtractor("plain", given);

        Assert.Equal(new ExtractorInfo(null, "scripted"), (await Call(extractor)).Info);
        Assert.Same(given, await Call(extractor));
    }

    [Fact]
    public async Task An_exception_step_is_thrown_and_still_counted()
    {
        var extractor = new ScriptedExtractor(new TimeoutException(), "ok");

        await Assert.ThrowsAsync<TimeoutException>(() => Call(extractor));
        Assert.Equal("ok", (await Call(extractor)).Text);
        Assert.Equal(2, extractor.Calls);
    }

    [Fact]
    public async Task A_function_answers_every_call_with_the_callers_token()
    {
        using var source = new CancellationTokenSource();
        var extractor = new ScriptedExtractor(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new ExtractorResponse("never", new ExtractorInfo(null, null));
        });

        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(extractor, source.Token));
    }

    [Fact]
    public async Task Requests_hold_the_prompt_and_context_of_each_call()
    {
        var extractor = new ScriptedExtractor("{}");

        await Call(extractor);

        var request = Assert.Single(extractor.Requests);
        Assert.Same(Context, request.Context);
        Assert.Contains("[l1] Anna: I like jazz.", request.Prompt.User, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_step_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ScriptedExtractor(42));
    }
}
