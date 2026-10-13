using Microsoft.Data.Sqlite;

namespace Switchknife.Journal;

/// <summary>The journal's tables. <c>user_version</c> holds the schema version; a newer one is refused, not guessed at.</summary>
internal static class Schema
{
    public const int Version = 1;

    private const string Create = """
        CREATE TABLE IF NOT EXISTS steps (
            flow     TEXT    NOT NULL,
            key      TEXT    NOT NULL,
            state    INTEGER NOT NULL,
            attempt  INTEGER NOT NULL,
            result   TEXT,
            error    TEXT,
            started  TEXT    NOT NULL,
            finished TEXT,
            PRIMARY KEY (flow, key)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS events (
            seq     INTEGER PRIMARY KEY AUTOINCREMENT,
            flow    TEXT NOT NULL,
            kind    TEXT NOT NULL,
            at      TEXT NOT NULL,
            payload TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS events_by_flow ON events (flow, seq);
        """;

    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken ct)
    {
        // WAL keeps readers off the writer's lock; FULL syncs every commit, because a lost result means a step runs twice.
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;", ct).ConfigureAwait(false);
        var version = await ScalarAsync(connection, "PRAGMA user_version;", ct).ConfigureAwait(false);
        if (version > Version)
            throw new InvalidOperationException($"The journal has schema version {version}; this library reads up to {Version}. Upgrade Switchknife.Journal.");
        if (version == Version)
            return;
        await ExecuteAsync(connection, $"{Create} PRAGMA user_version = {Version};", ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }
}
