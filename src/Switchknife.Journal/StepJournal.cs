using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;

namespace Switchknife.Journal;

/// <summary>
/// A durable record of the side effects of flows, in one SQLite file. <see cref="RunAsync{T}"/> records a step's intent
/// before it runs and its result after: a completed step never runs again and returns its recorded result; a step
/// whose earlier attempt never recorded an outcome runs again with <see cref="StepContext.Resumed"/> set, so it can
/// adopt or remove what that attempt left. Events append per flow and read back in order, so a restart can replay them.
/// </summary>
/// <remarks>
/// One process owns a journal file. Calls are serialized; run one step of a given key at a time. A step runs at least
/// once and its result is recorded at most once: a step that finished but crashed before its result was written runs
/// again, resumed. Make each step safe to repeat given its key (a deterministic resource name, "adopt or remove" first).
/// </remarks>
public sealed class StepJournal : IAsyncDisposable, IDisposable
{
    private const int MaxErrorLength = 2000;

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StepJournal(SqliteConnection connection, TimeProvider clock)
    {
        _connection = connection;
        _clock = clock;
    }

    /// <summary>Opens the journal at <paramref name="path"/>, creating the file and its tables when missing.</summary>
    public static async Task<StepJournal> OpenAsync(string path, TimeProvider? clock = null, CancellationToken ct = default)
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
        return new StepJournal(connection, clock ?? TimeProvider.System);
    }

    /// <summary>
    /// Runs <paramref name="step"/> once in effect: the recorded result when the key completed before, else a new attempt.
    /// A caller's cancellation leaves the step started, so the next run of the key is resumed; any other exception is
    /// recorded as a failure and rethrown.
    /// </summary>
    public async Task<T> RunAsync<T>(string flow, string key, Func<StepContext, CancellationToken, Task<T>> step, JsonTypeInfo<T> result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(result);
        var existing = await FindAsync(flow, key, ct).ConfigureAwait(false);
        if (existing is { State: StepState.Completed, Result: { } recorded })
            return JsonSerializer.Deserialize(recorded, result)!;

        var context = new StepContext(flow, key, (existing?.Attempt ?? 0) + 1, existing?.State == StepState.Started);
        await RecordIntentAsync(context, ct).ConfigureAwait(false);
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
            await RecordFailureAsync(context, e).ConfigureAwait(false);
            throw;
        }
        await RecordResultAsync(context, JsonSerializer.Serialize(value, result)).ConfigureAwait(false);
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
    public Task<bool> ResetAsync(string flow, string key, CancellationToken ct = default)
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

    /// <summary>The step as recorded, or null when the key never ran.</summary>
    public Task<StepRecord?> FindAsync(string flow, string key, CancellationToken ct = default)
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

    /// <summary>Steps whose intent is recorded and no outcome: on start, these are what a crash or a kill left. Null <paramref name="flow"/>: every flow.</summary>
    public Task<IReadOnlyList<StepRecord>> UnfinishedAsync(string? flow = null, CancellationToken ct = default) =>
        Locked(() => ReadStepsAsync(
            "SELECT flow, key, state, attempt, result, error, started, finished FROM steps WHERE state = 0 AND ($flow IS NULL OR flow = $flow) ORDER BY started, flow, key",
            [("$flow", flow)], ct), ct);

    /// <summary>Appends an event to <paramref name="flow"/> and returns its sequence number.</summary>
    public Task<long> AppendEventAsync(string flow, string kind, string payload, CancellationToken ct = default)
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

    /// <summary>The events of <paramref name="flow"/> after sequence <paramref name="after"/> (0: from the start), oldest first.</summary>
    public Task<IReadOnlyList<JournalEvent>> ReadEventsAsync(string flow, long after = 0, int limit = 1000, CancellationToken ct = default)
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

    /// <summary>Removes a flow's steps and events, once nothing will resume it. Returns how many rows went.</summary>
    public Task<int> ForgetAsync(string flow, CancellationToken ct = default)
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
    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private Task<int> RecordIntentAsync(StepContext step, CancellationToken ct) => Execute(
        """
        INSERT INTO steps (flow, key, state, attempt, result, error, started, finished) VALUES ($flow, $key, 0, $attempt, NULL, NULL, $now, NULL)
        ON CONFLICT (flow, key) DO UPDATE SET state = 0, attempt = $attempt, result = NULL, error = NULL, started = $now, finished = NULL
        """,
        [("$flow", step.Flow), ("$key", step.Key), ("$attempt", step.Attempt), ("$now", Format(_clock.GetUtcNow()))], ct);

    // The outcome is written even when the caller cancels now: the step's effect already happened.
    private Task<int> RecordResultAsync(StepContext step, string json) => Execute(
        "UPDATE steps SET state = 1, result = $result, finished = $now WHERE flow = $flow AND key = $key AND attempt = $attempt",
        [("$flow", step.Flow), ("$key", step.Key), ("$attempt", step.Attempt), ("$result", json), ("$now", Format(_clock.GetUtcNow()))], CancellationToken.None);

    private Task<int> RecordFailureAsync(StepContext step, Exception e)
    {
        var error = $"{e.GetType().Name}: {e.Message}";
        return Execute(
            "UPDATE steps SET state = 2, error = $error, finished = $now WHERE flow = $flow AND key = $key AND attempt = $attempt",
            [("$flow", step.Flow), ("$key", step.Key), ("$attempt", step.Attempt), ("$error", error.Length <= MaxErrorLength ? error : error[..MaxErrorLength]),
                ("$now", Format(_clock.GetUtcNow()))], CancellationToken.None);
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

[JsonSerializable(typeof(bool))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
