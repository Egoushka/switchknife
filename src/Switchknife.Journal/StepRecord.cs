namespace Switchknife.Journal;

/// <summary>Where a step stands in the journal.</summary>
public enum StepState
{
    /// <summary>Its intent is recorded and no outcome yet: it is running, or the attempt was interrupted.</summary>
    Started = 0,

    /// <summary>It finished and its result is recorded; it never runs again.</summary>
    Completed = 1,

    /// <summary>It threw; the next run of the same key is a new attempt.</summary>
    Failed = 2,
}

/// <summary>A step as the journal holds it.</summary>
/// <param name="Flow">The flow the step belongs to: a batch, a run, any unit that restarts as a whole.</param>
/// <param name="Key">The step's name inside its flow, stable across restarts (e.g. <c>prep/task-3</c>).</param>
/// <param name="State">Where the step stands.</param>
/// <param name="Attempt">1 for the first run of the key, one more for each run after it.</param>
/// <param name="Result">The recorded result as JSON, when <paramref name="State"/> is <see cref="StepState.Completed"/>.</param>
/// <param name="Error">What the last attempt threw, when <paramref name="State"/> is <see cref="StepState.Failed"/>.</param>
/// <param name="Started">When the latest attempt recorded its intent.</param>
/// <param name="Finished">When the latest attempt recorded its outcome; null while it is started.</param>
public sealed record StepRecord(
    string Flow, string Key, StepState State, int Attempt, string? Result, string? Error, DateTimeOffset Started, DateTimeOffset? Finished);

/// <summary>What a step is told when it runs.</summary>
/// <param name="Flow">The flow the step belongs to.</param>
/// <param name="Key">The step's name inside its flow.</param>
/// <param name="Attempt">1 for the first run of the key, one more for each run after it.</param>
/// <param name="Resumed">
/// An earlier attempt recorded its intent and never its outcome (the process crashed, was killed, or the caller cancelled).
/// Whatever that attempt started may exist: the step must adopt it or remove it before it acts again.
/// </param>
public sealed record StepContext(string Flow, string Key, int Attempt, bool Resumed);

/// <summary>One event of a flow, in the order it was appended.</summary>
/// <param name="Sequence">Increases across the whole journal; read on from the last one seen.</param>
/// <param name="Flow">The flow the event belongs to.</param>
/// <param name="Kind">The caller's name for what happened.</param>
/// <param name="At">When it was appended.</param>
/// <param name="Payload">The caller's JSON, stored as given.</param>
public sealed record JournalEvent(long Sequence, string Flow, string Kind, DateTimeOffset At, string Payload);
