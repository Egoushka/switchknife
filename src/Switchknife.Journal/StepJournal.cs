using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Switchknife.Journal;

/// <summary>
/// A durable record of the side effects of flows, backed by an <see cref="IStepStore"/>. <see cref="RunAsync{T}"/>
/// records a step's intent before it runs and its result after: a completed step never runs again and returns its
/// recorded result; a step whose earlier attempt never recorded an outcome runs again with
/// <see cref="StepContext.Resumed"/> set, so it can adopt or remove what that attempt left. Events append per flow
/// and read back in order, so a restart can replay them.
/// </summary>
/// <remarks>
/// Whether more than one process may run steps of the same journal at once, and how long a step runs at least once
/// without its result being recorded at most once, is the store's contract (see <see cref="SqliteStepStore"/>, one
/// file owned by one process; Switchknife.Journal.Postgres's <c>PgStepStore</c>, many processes sharing rows). Make
/// each step safe to repeat given its key (a deterministic resource name, "adopt or remove" first).
/// </remarks>
public sealed class StepJournal : IAsyncDisposable, IDisposable
{
    private readonly IStepStore _store;

    private StepJournal(IStepStore store)
    {
        _store = store;
    }

    /// <summary>Wraps an already-open <paramref name="store"/>: this journal owns it and disposes it with itself.</summary>
    public static StepJournal Open(IStepStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new StepJournal(store);
    }

    /// <summary>Opens the journal at <paramref name="path"/>, creating the SQLite file and its tables when missing.</summary>
    public static async Task<StepJournal> OpenAsync(string path, TimeProvider? clock = null, CancellationToken ct = default) =>
        new(await SqliteStepStore.OpenAsync(path, clock, ct).ConfigureAwait(false));

    /// <summary>
    /// Runs <paramref name="step"/> once in effect: the recorded result when the key completed before, else a new attempt.
    /// A caller's cancellation leaves the step started, so the next run of the key is resumed; any other exception is
    /// recorded as a failure and rethrown.
    /// </summary>
    public async Task<T> RunAsync<T>(string flow, string key, Func<StepContext, CancellationToken, Task<T>> step, JsonTypeInfo<T> result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(result);
        var claim = await _store.ClaimAsync(flow, key, ct).ConfigureAwait(false);
        if (claim.Status == StepClaimStatus.AlreadyCompleted)
            return JsonSerializer.Deserialize(claim.Result!, result)!;

        var context = new StepContext(flow, key, claim.Attempt, claim.Resumed);
        T value;
        try
        {
            value = await step(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            await _store.FailAsync(flow, key, claim.Attempt, $"{e.GetType().Name}: {e.Message}", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await _store.CompleteAsync(flow, key, claim.Attempt, JsonSerializer.Serialize(value, result), CancellationToken.None).ConfigureAwait(false);
        return value;
    }

    /// <summary><see cref="RunAsync{T}"/> for a step with no result worth keeping.</summary>
    public Task RunAsync(string flow, string key, Func<StepContext, CancellationToken, Task> step, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        return RunAsync(flow, key, async (context, token) =>
        {
            await step(context, token).ConfigureAwait(false);
            return true;
        }, JournalJsonContext.Default.Boolean, ct);
    }

    /// <summary>
    /// Removes the record of a completed or failed step, so the next <see cref="RunAsync{T}"/> of the key is a first attempt
    /// (<see cref="StepContext.Attempt"/> 1, <see cref="StepContext.Resumed"/> false). Returns false when the key had no record.
    /// Calling it states that the earlier effect is gone or may safely happen again; the journal cannot check that.
    /// </summary>
    /// <exception cref="InvalidOperationException">The step is <see cref="StepState.Started"/>: an interrupted attempt must be resumed, not forgotten.</exception>
    public Task<bool> ResetAsync(string flow, string key, CancellationToken ct = default) => _store.ResetAsync(flow, key, ct);

    /// <summary>The step as recorded, or null when the key never ran.</summary>
    public Task<StepRecord?> FindAsync(string flow, string key, CancellationToken ct = default) => _store.FindAsync(flow, key, ct);

    /// <summary>Steps whose intent is recorded and no outcome: on start, these are what a crash or a kill left. Null <paramref name="flow"/>: every flow.</summary>
    public Task<IReadOnlyList<StepRecord>> UnfinishedAsync(string? flow = null, CancellationToken ct = default) => _store.UnfinishedAsync(flow, ct);

    /// <summary>Appends an event to <paramref name="flow"/> and returns its sequence number.</summary>
    public Task<long> AppendEventAsync(string flow, string kind, string payload, CancellationToken ct = default) =>
        _store.AppendEventAsync(flow, kind, payload, ct);

    /// <summary>The events of <paramref name="flow"/> after sequence <paramref name="after"/> (0: from the start), oldest first.</summary>
    public Task<IReadOnlyList<JournalEvent>> ReadEventsAsync(string flow, long after = 0, int limit = 1000, CancellationToken ct = default) =>
        _store.ReadEventsAsync(flow, after, limit, ct);

    /// <summary>Removes a flow's steps and events, once nothing will resume it. Returns how many rows went.</summary>
    public Task<int> ForgetAsync(string flow, CancellationToken ct = default) => _store.ForgetAsync(flow, ct);

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _store.DisposeAsync();
}

[JsonSerializable(typeof(bool))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
