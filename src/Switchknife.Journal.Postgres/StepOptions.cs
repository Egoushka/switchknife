namespace Switchknife.Journal.Postgres;

/// <summary>How <see cref="PgStepStore"/> leases a step and waits out another process's lease.</summary>
public sealed record StepOptions
{
    /// <summary>The options a <see cref="PgStepStore"/> uses when none are given.</summary>
    public static StepOptions Default { get; } = new();

    /// <summary>
    /// How long a claim lasts before another process may reclaim it as abandoned. Longer than the slowest step;
    /// shorter means a crashed attempt is retried sooner.
    /// </summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times <see cref="PgStepStore.ClaimAsync"/> waits for another process's active lease before giving up.</summary>
    public int Retries { get; init; } = 50;

    /// <summary>How long <see cref="PgStepStore.ClaimAsync"/> waits between retries.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.FromMilliseconds(200);
}
