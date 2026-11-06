using System.Collections;

namespace Switchknife.Journal.Tests;

/// <summary>
/// The event stream's behaviour, one body for every store: <c>EventStreamTests</c> runs it on SQLite, the Postgres test project
/// links this file and runs it on <c>PgStepStore</c>. See <c>docs/specs/2026-11-06-journal-event-stream.md</c>, checks E1–E8.
/// </summary>
public abstract class EventStreamContract
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A journal on an empty store.</summary>
    protected abstract Task<StepJournal> OpenEmptyAsync();

    /// <summary>How many events a connection other than the journal's own sees committed.</summary>
    protected abstract Task<long> CommittedCountAsync();

    /// <summary>The query plan of the tag read for <paramref name="tag"/>, indexes allowed to win.</summary>
    protected abstract Task<string> ExplainTagReadAsync(string tag);

    /// <summary>The name of the index on <c>(tag, seq)</c>.</summary>
    protected abstract string TagIndexName { get; }

    [Fact]
    public async Task ReadAllEventsAsync_returns_the_events_of_every_flow_in_seq_order()
    {
        await using var journal = await OpenEmptyAsync();
        var appended = new List<long>();
        foreach (var (flow, n) in new[] { ("alpha", 1), ("beta", 2), ("gamma", 3), ("alpha", 4), ("gamma", 5), ("beta", 6) })
            appended.Add(await journal.AppendEventAsync(flow, "step", $$"""{"n":{{n}}}"""));

        var all = await journal.ReadAllEventsAsync();
        var firstPage = await journal.ReadAllEventsAsync(limit: 4);
        var secondPage = await journal.ReadAllEventsAsync(after: firstPage[^1].Sequence, limit: 4);

        Assert.Equal(appended, all.Select(e => e.Sequence));
        Assert.Equal(appended.OrderBy(s => s), all.Select(e => e.Sequence));
        Assert.Equal(["alpha", "beta", "gamma", "alpha", "gamma", "beta"], all.Select(e => e.Flow));
        Assert.Equal(appended.Take(4), firstPage.Select(e => e.Sequence));
        Assert.Equal(appended.Skip(4), secondPage.Select(e => e.Sequence));
        Assert.Empty(await journal.ReadAllEventsAsync(after: appended[^1]));
    }

    [Fact]
    public async Task A_repeat_append_with_the_same_flow_and_key_returns_the_first_seq_and_stores_nothing()
    {
        await using var journal = await OpenEmptyAsync();

        var first = await journal.AppendEventAsync("alpha", new NewEvent("done", """{"v":1}""", Key: "job-1"));
        var again = await journal.AppendEventAsync("alpha", new NewEvent("done", """{"v":2}""", Key: "job-1"));
        var otherFlow = await journal.AppendEventAsync("beta", new NewEvent("done", """{"v":3}""", Key: "job-1"));
        var unkeyed = await journal.AppendEventAsync("alpha", new NewEvent("done", """{"v":4}"""));
        var unkeyedToo = await journal.AppendEventAsync("alpha", new NewEvent("done", """{"v":4}"""));

        Assert.Equal(first, again);
        Assert.NotEqual(first, otherFlow);
        Assert.NotEqual(unkeyed, unkeyedToo);
        var stored = await journal.ReadAllEventsAsync();
        Assert.Equal(4, stored.Count);
        Assert.Equal("""{"v":1}""", stored.Single(e => e.Sequence == first).Payload);
    }

    [Fact]
    public async Task A_batch_of_500_is_one_transaction_and_one_signal()
    {
        await using var journal = await OpenEmptyAsync();
        var wake = journal.Signal.WaitAsync(0);
        Assert.False(wake.IsCompleted);

        var seqs = await journal.AppendEventsAsync("alpha", Batch(500));

        var woken = await wake.WaitAsync(Patience);
        Assert.Equal(500, seqs.Count);
        Assert.Equal(500, seqs.Distinct().Count());
        Assert.Equal(seqs.Max(), woken);
        Assert.Equal(500, await CommittedCountAsync());
        Assert.Equal(seqs, (await journal.ReadAllEventsAsync(limit: 1000)).Select(e => e.Sequence));
        await Assert.ThrowsAsync<TimeoutException>(() => journal.Signal.WaitAsync(woken).WaitAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_failing_batch_stores_nothing_and_raises_no_signal()
    {
        await using var journal = await OpenEmptyAsync();
        var wake = journal.Signal.WaitAsync(0);

        // The list passes the up-front checks (its enumerator is fine) and throws from the indexer partway through the transaction.
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendEventsAsync("alpha", new FailsAt(Batch(500), 300)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => journal.AppendEventsAsync("alpha", [new NewEvent("ok", "{}"), new NewEvent("", "{}")]));

        Assert.Equal(0, await CommittedCountAsync());
        Assert.Empty(await journal.ReadAllEventsAsync());
        Assert.False(wake.IsCompleted);
        var retried = await journal.AppendEventsAsync("alpha", Batch(3));
        Assert.Equal(3, retried.Count);
    }

    [Fact]
    public async Task A_repeated_batch_stores_nothing_and_raises_no_signal()
    {
        await using var journal = await OpenEmptyAsync();
        var first = await journal.AppendEventsAsync("alpha", Batch(500));
        var wake = journal.Signal.WaitAsync(first.Max());

        var again = await journal.AppendEventsAsync("alpha", Batch(500));
        var inBatch = await journal.AppendEventsAsync("beta", [new NewEvent("k", "{}", Key: "x"), new NewEvent("k", "{}", Key: "x")]);

        Assert.Equal(first, again);
        Assert.Equal(inBatch[0], inBatch[1]);
        Assert.Equal(501, await CommittedCountAsync());
        Assert.True(wake.IsCompleted, "the new key in 'beta' is a store and raises the signal");
        Assert.Equal(inBatch[0], await wake);
        var quiet = journal.Signal.WaitAsync(inBatch[0]);
        await journal.AppendEventsAsync("alpha", Batch(500));
        // agent-checks: allow sleep-in-test -- asserting that no signal arrives needs a window; a signal is raised before AppendEventsAsync returns, so this only guards late wake-ups
        await Task.Delay(100);
        Assert.False(quiet.IsCompleted);
    }

    [Fact]
    public async Task A_reader_awaiting_the_signal_wakes_after_the_commit_never_before()
    {
        await using var journal = await OpenEmptyAsync();
        var committedWhenWoken = Task.Run(async () =>
        {
            await journal.Signal.WaitAsync(0);
            return await CommittedCountAsync();
        });
        // agent-checks: allow sleep-in-test -- asserting that nothing wakes the reader before an append needs a window
        await Task.Delay(100);
        Assert.False(committedWhenWoken.IsCompleted);

        await journal.AppendEventsAsync("alpha", Batch(500));

        Assert.Equal(500, await committedWhenWoken.WaitAsync(Patience));
        var last = (await journal.ReadAllEventsAsync(limit: 1000))[^1].Sequence;
        Assert.Equal(last, await journal.Signal.WaitAsync(last - 1).WaitAsync(Patience));

        using var cancel = new CancellationTokenSource();
        var cancelled = journal.Signal.WaitAsync(last, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(last + 1, await WakeOnNext(journal, last));
    }

    [Fact]
    public async Task A_tag_filter_returns_only_that_tags_events()
    {
        await using var journal = await OpenEmptyAsync();
        var a1 = await journal.AppendEventAsync("alpha", new NewEvent("k", "{}", Tag: "a"));
        await journal.AppendEventAsync("alpha", new NewEvent("k", "{}", Tag: "b"));
        await journal.AppendEventAsync("beta", new NewEvent("k", "{}"));
        var a2 = await journal.AppendEventAsync("beta", new NewEvent("k", "{}", Tag: "a"));
        await journal.AppendEventAsync("gamma", "k", "{}");

        var tagged = await journal.ReadAllEventsAsync(tag: "a");

        Assert.Equal([a1, a2], tagged.Select(e => e.Sequence));
        Assert.All(tagged, e => Assert.Equal("a", e.Tag));
        Assert.Empty(await journal.ReadAllEventsAsync(tag: "nope"));
        Assert.Equal([a2], (await journal.ReadAllEventsAsync(after: a1, tag: "a")).Select(e => e.Sequence));
        Assert.Equal(["a", "b", null, "a", null], (await journal.ReadAllEventsAsync()).Select(e => e.Tag));
        Assert.Equal(["a", "b"], (await journal.ReadEventsAsync("alpha")).Select(e => e.Tag));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ReadAllEventsAsync(tag: ""));
    }

    [Fact]
    public async Task The_tag_read_uses_the_tag_index()
    {
        await using var journal = await OpenEmptyAsync();
        await journal.AppendEventsAsync("alpha", Enumerable.Range(0, 2000).Select(i => new NewEvent("k", "{}", Tag: $"tag-{i % 40}")).ToList());

        var plan = await ExplainTagReadAsync("tag-7");

        Assert.Contains(TagIndexName, plan);
    }

    private static async Task<long> WakeOnNext(StepJournal journal, long last)
    {
        var next = journal.Signal.WaitAsync(last);
        await journal.AppendEventAsync("alpha", "k", "{}");
        return await next.WaitAsync(Patience);
    }

    private static List<NewEvent> Batch(int count) =>
        Enumerable.Range(0, count).Select(i => new NewEvent("step", $$"""{"i":{{i}}}""", Key: $"e-{i}", Tag: i % 2 == 0 ? "even" : "odd")).ToList();

    private sealed class FailsAt(IReadOnlyList<NewEvent> inner, int index) : IReadOnlyList<NewEvent>
    {
        public int Count => inner.Count;

        public NewEvent this[int i] => i >= index ? throw new InvalidOperationException("the batch failed partway") : inner[i];

        public IEnumerator<NewEvent> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
