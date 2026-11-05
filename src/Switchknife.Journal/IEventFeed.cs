namespace Switchknife.Journal;

/// <summary>An event to append: what happened, its JSON, and optionally the key that makes appending it safe to repeat and a tag to filter by.</summary>
/// <param name="Kind">The caller's name for what happened.</param>
/// <param name="Payload">The caller's JSON, stored as given.</param>
/// <param name="Key">
/// Names this event inside its flow. A second append with the same flow and key stores nothing and returns the first
/// event's sequence number. Null: the event is always appended.
/// </param>
/// <param name="Tag">A label to read by (<see cref="IEventFeed.ReadAllEventsAsync"/>); null: untagged.</param>
public readonly record struct NewEvent(string Kind, string Payload, string? Key = null, string? Tag = null);

/// <summary>
/// The event stream over every flow, on top of the per-flow events of <see cref="IStepStore"/>. A store implements it
/// beside <see cref="IStepStore"/>; <see cref="StepJournal"/> throws <see cref="NotSupportedException"/> for the stream
/// when its store does not.
/// </summary>
public interface IEventFeed
{
    /// <summary>
    /// Appends <paramref name="events"/> to <paramref name="flow"/> in one transaction: all or none. Returns one sequence number
    /// per event, in input order; for an event whose key the flow already has (earlier, or earlier in this batch) that is the
    /// first event's number and nothing is stored for it. <see cref="Signal"/> is raised once, after the commit, and only
    /// when the batch stored something.
    /// </summary>
    Task<IReadOnlyList<long>> AppendEventsAsync(string flow, IReadOnlyList<NewEvent> events, CancellationToken ct);

    /// <summary>
    /// The events of every flow after sequence <paramref name="after"/>, oldest first, at most <paramref name="limit"/>.
    /// With <paramref name="tag"/>: only events carrying that tag.
    /// </summary>
    Task<IReadOnlyList<JournalEvent>> ReadAllEventsAsync(long after, int limit, string? tag, CancellationToken ct);

    /// <summary>Wakes readers when this store instance commits events.</summary>
    IEventSignal Signal { get; }
}

/// <summary>
/// A hint that events were committed. It carries no events: read, then wait with the last sequence number seen, then read again.
/// It is raised after the commit, never before, and only for commits made through the same store instance; a reader in
/// another process polls.
/// </summary>
public interface IEventSignal
{
    /// <summary>
    /// Completes with the highest committed sequence number this signal has seen, once it is greater than <paramref name="after"/>
    /// (at once when it already is).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while waiting.</exception>
    Task<long> WaitAsync(long after, CancellationToken ct = default);
}

/// <summary>The <see cref="IEventSignal"/> a store raises after each commit that stored events.</summary>
public sealed class EventSignal : IEventSignal
{
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private long _latest;

    /// <summary>Records that events up to <paramref name="sequence"/> are committed and wakes the readers waiting below it.</summary>
    public void Notify(long sequence)
    {
        List<Waiter>? woken = null;
        lock (_gate)
        {
            if (sequence > _latest)
                _latest = sequence;
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].After >= _latest)
                    continue;
                (woken ??= []).Add(_waiters[i]);
                _waiters.RemoveAt(i);
            }
        }
        if (woken is null)
            return;
        foreach (var waiter in woken)
            waiter.Completion.TrySetResult(_latest);
    }

    /// <inheritdoc />
    public Task<long> WaitAsync(long after, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (_latest > after)
                return Task.FromResult(_latest);
            waiter = new Waiter(after, new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously));
            _waiters.Add(waiter);
        }
        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(static state =>
            {
                var (signal, pending, token) = ((EventSignal, Waiter, CancellationToken))state!;
                lock (signal._gate)
                    signal._waiters.Remove(pending);
                pending.Completion.TrySetCanceled(token);
            }, (this, waiter, ct));
            waiter.Completion.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(), registration,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return waiter.Completion.Task;
    }

    private sealed record Waiter(long After, TaskCompletionSource<long> Completion);
}
