namespace Switchknife.Knowledge.Tests;

public sealed class QueryTermsTests
{
    [Fact]
    public void A_sentence_keeps_its_content_words_in_order()
    {
        Assert.Equal(["keep", "scheduler", "stampeding", "jobs"], QueryTerms.Of("What should we use to keep a scheduler from stampeding the jobs?"));
    }

    [Fact]
    public void A_trailing_plural_s_is_folded_on_long_words_only()
    {
        Assert.Equal(["retrie", "class", "jobs"], QueryTerms.Of("retries class jobs"));
    }

    [Fact]
    public void Filler_short_words_and_repeats_are_dropped()
    {
        Assert.Equal(["queue", "backoff"], QueryTerms.Of("the queue, a queue and ok backoff"));
    }

    [Fact]
    public void Punctuation_separates_words_and_case_is_ignored()
    {
        Assert.Equal(["rate", "limit", "token", "bucket"], QueryTerms.Of("Rate-Limit_TOKEN bucket"));
    }

    [Fact]
    public void A_long_question_is_capped()
    {
        var question = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"word{i}"));

        Assert.Equal(24, QueryTerms.Of(question).Count);
    }
}
