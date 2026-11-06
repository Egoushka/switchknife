using Microsoft.Data.Sqlite;

namespace Switchknife.Journal;

/// <summary>The journal's tables. <c>user_version</c> holds the schema version; a newer one is refused, not guessed at.</summary>
internal static class Schema
{
    public const int Version = 2;

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
            payload TEXT NOT NULL,
            tag     TEXT,
            key     TEXT
        );
        CREATE INDEX IF NOT EXISTS events_by_flow ON events (flow, seq);
        CREATE INDEX IF NOT EXISTS events_by_tag ON events (tag, seq);
        CREATE UNIQUE INDEX IF NOT EXISTS events_by_key ON events (flow, key) WHERE key IS NOT NULL;
        """;

    // Version 1 had no tag and no key. The additive upgrade runs in one transaction, so a crash leaves version 1 intact.
    private const string UpgradeFrom1 = """
        BEGIN;
        ALTER TABLE events ADD COLUMN tag TEXT;
        ALTER TABLE events ADD COLUMN key TEXT;
        CREATE INDEX IF NOT EXISTS events_by_tag ON events (tag, seq);
        CREATE UNIQUE INDEX IF NOT EXISTS events_by_key ON events (flow, key) WHERE key IS NOT NULL;
        PRAGMA user_version = 2;
        COMMIT;
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
        await ExecuteAsync(connection, version == 1 ? UpgradeFrom1 : $"{Create} PRAGMA user_version = {Version};", ct).ConfigureAwait(false);
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
