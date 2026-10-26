using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Switchknife.Journal;

/// <summary>
/// <see cref="IStepStore"/> on one SQLite file. One process owns the file; calls are serialized inside this store so
/// that <see cref="ClaimAsync"/> is atomic even though SQLite itself does not arbitrate between processes.
/// </summary>
public sealed class SqliteStepStore : IStepStore
{
    private const int MaxErrorLength = 2000;

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SqliteStepStore(SqliteConnection connection, TimeProvider clock)
    {
        _connection = connection;
        _clock = clock;
    }

    /// <summary>Opens the store at <paramref name="path"/>, creating the file and its tables when missing.</summary>
    public static async Task<SqliteStepStore> OpenAsync(string path, TimeProvider? clock = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await Schema.EnsureAsync(connection, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new SqliteStepStore(connection, clock ?? TimeProvider.System);
    }

    /// <inheritdoc />
    public Task<StepClaim> ClaimAsync(string flow, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Locked(async () =>
        {
            var rows = await ReadStepsAsync("SELECT flow, key, state, attempt, result, error, started, finished FROM steps WHERE flow = $flow AND key = $key",
                [("$flow", flow), ("$key", key)], ct).ConfigureAwait(false);
            var existing = rows.Count == 0 ? null : rows[0];
            if (existing is { State: StepState.Completed, Result: { } recorded })
                return new StepClaim(StepClaimStatus.AlreadyCompleted, existing.Attempt, false, recorded);

            var attempt = (existing?.Attempt ?? 0) + 1;
            var resumed = existing?.State == StepState.Started;
            var command = Command(
                """
                INSERT INTO steps (flow, key, state, attempt, result, error, started, finished) VALUES ($flow, $key, 0, $attempt, NULL, NULL, $now, NULL)
                ON CONFLICT (flow, key) DO UPDATE SET state = 0, attempt = $attempt, result = NULL, error = NULL, started = $now, finished = NULL
                """,
                [("$flow", flow), ("$key", key), ("$attempt", attempt), ("$now", Format(_clock.GetUtcNow()))]);
            await using var commandScope = command.ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new StepClaim(StepClaimStatus.Claimed, attempt, resumed, null);
        }, ct);
    }

    /// <inheritdoc />
    // The outcome is written even when the caller cancels now: the step's effect already happened.
    public Task CompleteAsync(string flow, string key, int attempt, string result, CancellationToken ct) => Execute(
        "UPDATE steps SET state = 1, result = $result, finished = $now WHERE flow = $flow AND key = $key AND attempt = $attempt",
        [("$flow", flow), ("$key", key), ("$attempt", attempt), ("$result", result), ("$now", Format(_clock.GetUtcNow()))], CancellationToken.None);

    /// <inheritdoc />
    public Task FailAsync(string flow, string key, int attempt, string failure, CancellationToken ct) => Execute(
        "UPDATE steps SET state = 2, error = $error, finished = $now WHERE flow = $flow AND key = $key AND attempt = $attempt",
        [("$flow", flow), ("$key", key), ("$attempt", attempt), ("$error", failure.Length <= MaxErrorLength ? failure : failure[..MaxErrorLength]),
            ("$now", Format(_clock.GetUtcNow()))], CancellationToken.None);

    /// <inheritdoc />
    public Task<bool> ResetAsync(string flow, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Locked(async () =>
        {
            var command = Command("DELETE FROM steps WHERE flow = $flow AND key = $key AND state <> 0", [("$flow", flow), ("$key", key)]);
            await using var commandScope = command.ConfigureAwait(false);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0)
                return true;
            var left = await ReadStepsAsync("SELECT flow, key, state, attempt, result, error, started, finished FROM steps WHERE flow = $flow AND key = $key",
                [("$flow", flow), ("$key", key)], ct).ConfigureAwait(false);
            return left.Count == 0
                ? false
                : throw new InvalidOperationException($"Step '{flow}/{key}' is started: resume it (run it again) instead of resetting it.");
        }, ct);
    }

    /// <inheritdoc />
    public Task<StepRecord?> FindAsync(string flow, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Locked(async () =>
        {
            var rows = await ReadStepsAsync("SELECT flow, key, state, attempt, result, error, started, finished FROM steps WHERE flow = $flow AND key = $key",
                [("$flow", flow), ("$key", key)], ct).ConfigureAwait(false);
            return rows.Count == 0 ? null : rows[0];
        }, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StepRecord>> UnfinishedAsync(string? flow, CancellationToken ct) =>
        Locked(() => ReadStepsAsync(
            "SELECT flow, key, state, attempt, result, error, started, finished FROM steps WHERE state = 0 AND ($flow IS NULL OR flow = $flow) ORDER BY started, flow, key",
            [("$flow", flow)], ct), ct);

    /// <inheritdoc />
    public Task<long> AppendEventAsync(string flow, string kind, string payload, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(payload);
        return Locked(async () =>
        {
            var command = Command("INSERT INTO events (flow, kind, at, payload) VALUES ($flow, $kind, $at, $payload) RETURNING seq",
                [("$flow", flow), ("$kind", kind), ("$at", Format(_clock.GetUtcNow())), ("$payload", payload)]);
            await using var commandScope = command.ConfigureAwait(false);
            return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JournalEvent>> ReadEventsAsync(string flow, long after, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return Locked<IReadOnlyList<JournalEvent>>(async () =>
        {
            var command = Command("SELECT seq, flow, kind, at, payload FROM events WHERE flow = $flow AND seq > $after ORDER BY seq LIMIT $limit",
                [("$flow", flow), ("$after", after), ("$limit", limit)]);
            await using var commandScope = command.ConfigureAwait(false);
            var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            var events = new List<JournalEvent>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                events.Add(new JournalEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)), reader.GetString(4)));
            return events;
        }, ct);
    }

    /// <inheritdoc />
    public Task<int> ForgetAsync(string flow, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(flow);
        return Locked(async () =>
        {
            var command = Command("DELETE FROM steps WHERE flow = $flow; DELETE FROM events WHERE flow = $flow;", [("$flow", flow)]);
            await using var commandScope = command.ConfigureAwait(false);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private Task<int> Execute(string sql, (string Name, object? Value)[] parameters, CancellationToken ct) => Locked(async () =>
    {
        var command = Command(sql, parameters);
        await using var commandScope = command.ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }, ct);

    private async Task<IReadOnlyList<StepRecord>> ReadStepsAsync(string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        var command = Command(sql, parameters);
        await using var commandScope = command.ConfigureAwait(false);
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        var steps = new List<StepRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            steps.Add(new StepRecord(reader.GetString(0), reader.GetString(1), (StepState)reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7))));
        }
        return steps;
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private async Task<T> Locked<T>(Func<Task<T>> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Format(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string at) => DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
