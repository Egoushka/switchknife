using System.Reflection;
using Npgsql;
using NpgsqlTypes;
using Switchknife.Journal.Postgres;

namespace Switchknife.Journal.Bench;

/// <summary>One way to append a hash-chained (or, for the baselines, plain) event to a room.</summary>
internal interface IAppender : IAsyncDisposable
{
    string Name { get; }

    Task AppendAsync(string room, string sourceId, string body, CancellationToken ct);
}

/// <summary>The chain in memory: one lock, a dictionary of rooms. The floor: hashing and bookkeeping, no I/O.</summary>
internal sealed class MemoryChainAppender : IAppender
{
    private readonly Dictionary<string, (long Seq, byte[] Hash)> _rooms = [];
    private readonly List<(string Room, long Seq, byte[] BodyHash, byte[] PrevHash, byte[] Hash, string Body)> _log = [];
    private readonly Lock _gate = new();

    public string Name => "memory, chained";

    public Task AppendAsync(string room, string sourceId, string body, CancellationToken ct)
    {
        var bodyHash = Chain.BodyHash(body);
        lock (_gate)
        {
            var (seq, prev) = _rooms.GetValueOrDefault(room, (0, Chain.Genesis));
            seq++;
            var hash = Chain.Link(room, seq, DateTimeOffset.UtcNow, "claude", "tool_result", [], "none", sourceId, bodyHash, prev);
            _rooms[room] = (seq, hash);
            _log.Add((room, seq, bodyHash, prev, hash, body));
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// ADR 0007 decision 1 and 2 as written: a transaction takes an advisory lock on the room, reads the last <c>room_seq</c> and
/// <c>hash</c>, and inserts the next event. Two round trips: begin, lock and read in one batch; insert and commit in the next.
/// </summary>
internal sealed class PgChainAppender(NpgsqlDataSource data) : IAppender
{
    public string Name => "Postgres, chained (modelled 0002_rooms)";

    /// <summary>Applies the library's own 0001 migration, then the columns and indexes decision 1 adds (a bench-local copy of the not yet written 0002).</summary>
    public static async Task CreateSchemaAsync(NpgsqlDataSource data)
    {
        var assembly = typeof(PgStepStore).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0001_steps.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var steps = await reader.ReadToEndAsync().ConfigureAwait(false);
        await Run(data, "drop table if exists steps, step_events").ConfigureAwait(false);
        await Run(data, steps).ConfigureAwait(false);
        await Run(data, """
            alter table step_events
                add column room_seq bigint, add column ts timestamptz, add column actor text, add column refs text[],
                add column undo text, add column source_id text, add column body_hash bytea, add column prev_hash bytea, add column hash bytea;
            create unique index step_events_room_seq on step_events (flow, room_seq) where room_seq is not null;
            create unique index step_events_source on step_events (flow, source_id) where source_id is not null;
            """).ConfigureAwait(false);
    }

    public async Task AppendAsync(string room, string sourceId, string body, CancellationToken ct)
    {
        var connection = await data.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var scope = connection.ConfigureAwait(false);
        try
        {
            long seq = 0;
            var prev = Chain.Genesis;
            var read = new NpgsqlBatch(connection);
            await using (read.ConfigureAwait(false))
            {
                read.BatchCommands.Add(new NpgsqlBatchCommand("begin"));
                var lockCommand = new NpgsqlBatchCommand("select pg_advisory_xact_lock(hashtextextended(@room, 0))");
                lockCommand.Parameters.AddWithValue("room", room);
                read.BatchCommands.Add(lockCommand);
                var last = new NpgsqlBatchCommand("select room_seq, hash from step_events where flow = @room and room_seq is not null order by room_seq desc limit 1");
                last.Parameters.AddWithValue("room", room);
                read.BatchCommands.Add(last);
                var results = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (results.ConfigureAwait(false))
                {
                    // The result sets are begin (no columns), the lock (one column) and the last event (two columns, a row when the room is not empty).
                    do
                    {
                        if (results.FieldCount == 2 && await results.ReadAsync(ct).ConfigureAwait(false))
                        {
                            seq = results.GetInt64(0);
                            prev = results.GetFieldValue<byte[]>(1);
                        }
                    }
                    while (await results.NextResultAsync(ct).ConfigureAwait(false));
                }
            }

            seq++;
            var ts = DateTimeOffset.UtcNow;
            var bodyHash = Chain.BodyHash(body);
            var hash = Chain.Link(room, seq, ts, "claude", "tool_result", [], "none", sourceId, bodyHash, prev);
            var write = new NpgsqlBatch(connection);
            await using (write.ConfigureAwait(false))
            {
                var insert = new NpgsqlBatchCommand(
                    """
                    insert into step_events (flow, kind, at, payload, room_seq, ts, actor, refs, undo, source_id, body_hash, prev_hash, hash)
                    values (@flow, @kind, @at, @payload, @seq, @ts, @actor, @refs, @undo, @source, @body_hash, @prev_hash, @hash)
                    """);
                insert.Parameters.AddWithValue("flow", room);
                insert.Parameters.AddWithValue("kind", "tool_result");
                insert.Parameters.AddWithValue("at", ts);
                insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Json) { Value = body });
                insert.Parameters.AddWithValue("seq", seq);
                insert.Parameters.AddWithValue("ts", ts);
                insert.Parameters.AddWithValue("actor", "claude");
                insert.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = Array.Empty<string>() });
                insert.Parameters.AddWithValue("undo", "none");
                insert.Parameters.AddWithValue("source", sourceId);
                insert.Parameters.AddWithValue("body_hash", bodyHash);
                insert.Parameters.AddWithValue("prev_hash", prev);
                insert.Parameters.AddWithValue("hash", hash);
                write.BatchCommands.Add(insert);
                write.BatchCommands.Add(new NpgsqlBatchCommand("commit"));
                await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await using var rollback = new NpgsqlCommand("rollback", connection);
            await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task Run(NpgsqlDataSource data, string sql)
    {
        await using var command = data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

/// <summary>What ships today: <see cref="IStepStore.AppendEventAsync"/> as it is, no chain. The baseline the chain is measured against.</summary>
internal sealed class PlainAppender(IStepStore store, string name) : IAppender
{
    public string Name => name;

    public Task AppendAsync(string room, string sourceId, string body, CancellationToken ct) => store.AppendEventAsync(room, "tool_result", body, ct);

    public ValueTask DisposeAsync() => store.DisposeAsync();
}
