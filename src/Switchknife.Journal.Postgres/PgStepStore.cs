using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using NpgsqlTypes;

namespace Switchknife.Journal.Postgres;

/// <summary>
/// <see cref="IStepStore"/> on PostgreSQL: many processes share one <c>steps</c> table, each row leased
/// (<c>lease_until</c>) instead of locked by an in-process semaphore. Apply <c>Migrations/0001_steps.sql</c> (embedded
/// in this assembly) to the database first, with Switchknife.Migrations or any other migrator.
/// </summary>
/// <remarks>
/// <see cref="ClaimAsync"/> opens its own short transaction per attempt: it locks the row (<c>SELECT ... FOR UPDATE</c>),
/// decides from its state and lease whether to hand out a claim, and commits before the step itself runs, so the
/// row is not held locked for the step's duration. A lease that is still active when another process asks belongs to
/// whichever process is currently running the step; <see cref="ClaimAsync"/> waits it out
/// (<see cref="StepOptions.Retries"/>, <see cref="StepOptions.Backoff"/>) rather than stealing it. This store does not
/// own <see cref="NpgsqlDataSource"/>: <see cref="DisposeAsync"/> does nothing to it, since it is typically the host
/// application's own, shared data source.
/// </remarks>
public sealed class PgStepStore : IStepStore
{
    private const int MaxErrorLength = 2000;

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;
    private readonly StepOptions _options;

    /// <summary>A store on <paramref name="dataSource"/>'s database, whose <c>steps</c> and <c>step_events</c> tables already exist.</summary>
    public PgStepStore(NpgsqlDataSource dataSource, TimeProvider? clock = null, StepOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _clock = clock ?? TimeProvider.System;
        _options = options ?? StepOptions.Default;
    }

    /// <inheritdoc />
    public async Task<StepClaim> ClaimAsync(string flow, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(key);
        for (var tries = 0; ; tries++)
        {
            var claim = await TryClaimAsync(flow, key, ct).ConfigureAwait(false);
            if (claim is { } made)
                return made;
            if (tries >= _options.Retries)
            {
                throw new TimeoutException(
                    $"Step '{flow}/{key}' is leased by another process; gave up after {_options.Retries} retr{(_options.Retries == 1 ? "y" : "ies")}.");
            }
            await Task.Delay(_options.Backoff, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One attempt at a claim. Null means another process holds an active lease: the caller should wait and retry.</summary>
    private async Task<StepClaim?> TryClaimAsync(string flow, string key, CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        var existing = await SelectForUpdateAsync(connection, transaction, flow, key, ct).ConfigureAwait(false);
        if (existing is { State: StepState.Completed, Result: { } recorded })
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new StepClaim(StepClaimStatus.AlreadyCompleted, existing.Value.Attempt, false, recorded);
        }
        if (existing is { State: StepState.Started } started && started.LeaseUntil > now)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var attempt = (existing?.Attempt ?? 0) + 1;
        var resumed = existing?.State == StepState.Started;
        var leaseUntil = now + _options.Lease;
        var command = existing is null
            // ON CONFLICT DO NOTHING: the row did not exist under our FOR UPDATE (nothing to lock), but another
            // transaction may insert the same key before we commit; its insert then blocks on the unique index and,
            // once it commits, ours affects zero rows instead of raising a unique-violation.
            ? new NpgsqlCommand(
                """
                insert into steps (flow, key, state, attempt, result, error, started, finished, lease_until)
                values (@flow, @key, 0, 1, null, null, @now, null, @lease)
                on conflict (flow, key) do nothing
                """,
                connection, transaction)
            : new NpgsqlCommand(
                """
                update steps set state = 0, attempt = @attempt, result = null, error = null, started = @now, finished = null, lease_until = @lease
                where flow = @flow and key = @key
                """,
                connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("flow", flow);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease", leaseUntil);
        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected == 0)
        {
            // Lost the race to insert a brand-new key: someone else's row is there now. Retry; we will see it this time.
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new StepClaim(StepClaimStatus.Claimed, attempt, resumed, null);
    }

    /// <inheritdoc />
    // The outcome is written even when the caller cancels now: the step's effect already happened.
    public async Task CompleteAsync(string flow, string key, int attempt, string result, CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(
            "update steps set state = 1, result = @result, finished = @now where flow = @flow and key = @key and attempt = @attempt", connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("flow", flow);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Json) { Value = result });
        command.Parameters.AddWithValue("now", _clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FailAsync(string flow, string key, int attempt, string failure, CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(
            "update steps set state = 2, error = @error, finished = @now where flow = @flow and key = @key and attempt = @attempt", connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("flow", flow);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("error", failure.Length <= MaxErrorLength ? failure : failure[..MaxErrorLength]);
        command.Parameters.AddWithValue("now", _clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ResetAsync(string flow, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand("delete from steps where flow = @flow and key = @key and state <> 0 returning 1", connection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("flow", flow);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return true;
        }

        var left = await FindAsync(flow, key, ct).ConfigureAwait(false);
        return left is null
            ? false
            : throw new InvalidOperationException($"Step '{flow}/{key}' is started: resume it (run it again) instead of resetting it.");
    }

    /// <inheritdoc />
    public async Task<StepRecord?> FindAsync(string flow, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(
            "select flow, key, state, attempt, result, error, started, finished from steps where flow = @flow and key = @key", connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("flow", flow);
        command.Parameters.AddWithValue("key", key);
        var rows = await ReadStepsAsync(command, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StepRecord>> UnfinishedAsync(string? flow, CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(
            "select flow, key, state, attempt, result, error, started, finished from steps where state = 0 and (@flow::text is null or flow = @flow) order by started, flow, key",
            connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new NpgsqlParameter("flow", NpgsqlDbType.Text) { Value = (object?)flow ?? DBNull.Value });
        return await ReadStepsAsync(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> AppendEventAsync(string flow, string kind, string payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(payload);
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(
            "insert into step_events (flow, kind, at, payload) values (@flow, @kind, @at, @payload) returning seq", connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("flow", flow);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("at", _clock.GetUtcNow());
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Json) { Value = payload });
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JournalEvent>> ReadEventsAsync(string flow, long after, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(
            "select seq, flow, kind, at, payload from step_events where flow = @flow and seq > @after order by seq limit @limit", connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("flow", flow);
        command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("limit", limit);
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        var events = new List<JournalEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new JournalEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4)));
        }
        return events;
    }

    /// <inheritdoc />
    public async Task<int> ForgetAsync(string flow, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var steps = new NpgsqlCommand("delete from steps where flow = @flow", connection, transaction);
        await using (steps.ConfigureAwait(false))
        {
            steps.Parameters.AddWithValue("flow", flow);
            var removed = await steps.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var events = new NpgsqlCommand("delete from step_events where flow = @flow", connection, transaction);
            await using (events.ConfigureAwait(false))
            {
                events.Parameters.AddWithValue("flow", flow);
                removed += await events.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return removed;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="step"/> and writes its result in <paramref name="transaction"/>: both commit or roll back
    /// together, so a DB-only step is exactly-once with no separate lease and nothing to resume. The recorded result
    /// when the key completed before (no claim is taken; <paramref name="step"/> does not run).
    /// </summary>
    public async Task<T> RunInTransactionAsync<T>(NpgsqlTransaction transaction, string flow, string key,
        Func<StepContext, NpgsqlTransaction, CancellationToken, Task<T>> step, JsonTypeInfo<T> result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var connection = transaction.Connection ?? throw new ArgumentException("The transaction has no open connection.", nameof(transaction));

        var existing = await SelectForUpdateAsync(connection, transaction, flow, key, ct).ConfigureAwait(false);
        if (existing is { State: StepState.Completed, Result: { } recorded })
            return JsonSerializer.Deserialize(recorded, result)!;

        var attempt = (existing?.Attempt ?? 0) + 1;
        var context = new StepContext(flow, key, attempt, existing?.State == StepState.Started);
        var value = await step(context, transaction, ct).ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        var json = JsonSerializer.Serialize(value, result);
        var upsert = existing is null
            ? new NpgsqlCommand(
                "insert into steps (flow, key, state, attempt, result, error, started, finished, lease_until) values (@flow, @key, 1, @attempt, @result, null, @now, @now, @now)",
                connection, transaction)
            : new NpgsqlCommand(
                "update steps set state = 1, attempt = @attempt, result = @result, error = null, finished = @now, lease_until = @now where flow = @flow and key = @key",
                connection, transaction);
        await using var upsertScope = upsert.ConfigureAwait(false);
        upsert.Parameters.AddWithValue("flow", flow);
        upsert.Parameters.AddWithValue("key", key);
        upsert.Parameters.AddWithValue("attempt", attempt);
        upsert.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Json) { Value = json });
        upsert.Parameters.AddWithValue("now", now);
        await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return value;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<ExistingRow?> SelectForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string flow, string key, CancellationToken ct)
    {
        var command = new NpgsqlCommand("select state, attempt, result, lease_until from steps where flow = @flow and key = @key for update", connection, transaction);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("flow", flow);
        command.Parameters.AddWithValue("key", key);
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new ExistingRow((StepState)reader.GetInt16(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    private static async Task<IReadOnlyList<StepRecord>> ReadStepsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        var steps = new List<StepRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            steps.Add(new StepRecord(reader.GetString(0), reader.GetString(1), (StepState)reader.GetInt16(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return steps;
    }

    private readonly record struct ExistingRow(StepState State, int Attempt, string? Result, DateTimeOffset LeaseUntil);
}
