namespace Switchknife.Journal;

/// <summary>
/// The durable storage behind <see cref="StepJournal"/>. A store need not serialize calls for different keys itself
/// (the journal already lets different keys run at once); it must make <see cref="ClaimAsync"/> atomic for a given
/// key so that only one caller of it, in this process or another, ever gets <see cref="StepClaimStatus.Claimed"/> for
/// a lease at a time.
/// </summary>
public interface IStepStore : IAsyncDisposable
{
    /// <summary>
    /// Claims <paramref name="key"/> of <paramref name="flow"/> to run: the recorded result when it completed before,
    /// else a new attempt whose intent is now recorded. A store that supports more than one process must not hand out
    /// a second claim for the same key while an earlier one is still in effect.
    /// </summary>
    Task<StepClaim> ClaimAsync(string flow, string key, CancellationToken ct);

    /// <summary>Records the result of the attempt <see cref="ClaimAsync"/> returned, as JSON.</summary>
    Task CompleteAsync(string flow, string key, int attempt, string result, CancellationToken ct);

    /// <summary>Records that the attempt <see cref="ClaimAsync"/> returned threw.</summary>
    Task FailAsync(string flow, string key, int attempt, string failure, CancellationToken ct);

    /// <summary>Removes the record of a completed or failed step. Returns false when the key had no record.</summary>
    /// <exception cref="InvalidOperationException">The step is started: it must be resumed, not forgotten.</exception>
    Task<bool> ResetAsync(string flow, string key, CancellationToken ct);

    /// <summary>The step as recorded, or null when the key never ran.</summary>
    Task<StepRecord?> FindAsync(string flow, string key, CancellationToken ct);

    /// <summary>Steps whose intent is recorded and no outcome. Null <paramref name="flow"/>: every flow.</summary>
    Task<IReadOnlyList<StepRecord>> UnfinishedAsync(string? flow, CancellationToken ct);

    /// <summary>Appends an event to <paramref name="flow"/> and returns its sequence number.</summary>
    Task<long> AppendEventAsync(string flow, string kind, string payload, CancellationToken ct);

    /// <summary>The events of <paramref name="flow"/> after sequence <paramref name="after"/>, oldest first.</summary>
    Task<IReadOnlyList<JournalEvent>> ReadEventsAsync(string flow, long after, int limit, CancellationToken ct);

    /// <summary>Removes a flow's steps and events. Returns how many rows went.</summary>
    Task<int> ForgetAsync(string flow, CancellationToken ct);
}

/// <summary>What <see cref="IStepStore.ClaimAsync"/> handed out.</summary>
public enum StepClaimStatus
{
    /// <summary>A new attempt was recorded; run the step.</summary>
    Claimed,

    /// <summary>The key completed before; <see cref="StepClaim.Result"/> is its recorded result.</summary>
    AlreadyCompleted,
}

/// <summary>The outcome of a claim.</summary>
/// <param name="Status">Whether a new attempt was recorded or the key was already complete.</param>
/// <param name="Attempt">The attempt number: meaningful only when <paramref name="Status"/> is <see cref="StepClaimStatus.Claimed"/>.</param>
/// <param name="Resumed">An earlier attempt recorded its intent and never its outcome. Meaningful only when claimed.</param>
/// <param name="Result">The recorded result as JSON, when <paramref name="Status"/> is <see cref="StepClaimStatus.AlreadyCompleted"/>.</param>
public readonly record struct StepClaim(StepClaimStatus Status, int Attempt, bool Resumed, string? Result);
