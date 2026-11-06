using Microsoft.Data.Sqlite;

namespace Switchknife.Journal.Tests;

/// <summary>The event stream on <see cref="SqliteStepStore"/>; the shared body is <see cref="EventStreamContract"/>.</summary>
public sealed class EventStreamTests : EventStreamContract, IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("switchknife-events").FullName;
    private readonly List<StepJournal> _opened = [];

    private string Path => System.IO.Path.Combine(_dir, "journal.db");

    protected override string TagIndexName => "events_by_tag";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var journal in _opened)
            await journal.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    protected override async Task<StepJournal> OpenEmptyAsync()
    {
        var journal = await StepJournal.OpenAsync(Path);
        _opened.Add(journal);
        return journal;
    }

    protected override async Task<long> CommittedCountAsync()
    {
        await using var connection = Connect();
        await connection.OpenAsync();
        await using var command = new SqliteCommand("SELECT COUNT(*) FROM events", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    protected override async Task<string> ExplainTagReadAsync(string tag)
    {
        await using var connection = Connect();
        await connection.OpenAsync();
        await using var command = new SqliteCommand("EXPLAIN QUERY PLAN " + SqliteStepStore.ReadByTagSql, connection);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$after", 0L);
        command.Parameters.AddWithValue("$limit", 1000);
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(3));
        return string.Join("; ", plan);
    }

    [Fact]
    public async Task A_version_1_file_upgrades_on_open_and_keeps_its_events()
    {
        await using (var old = Connect())
        {
            await old.OpenAsync();
            await using var create = new SqliteCommand(
                """
                CREATE TABLE steps (flow TEXT NOT NULL, key TEXT NOT NULL, state INTEGER NOT NULL, attempt INTEGER NOT NULL, result TEXT, error TEXT,
                    started TEXT NOT NULL, finished TEXT, PRIMARY KEY (flow, key)) WITHOUT ROWID;
                CREATE TABLE events (seq INTEGER PRIMARY KEY AUTOINCREMENT, flow TEXT NOT NULL, kind TEXT NOT NULL, at TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX events_by_flow ON events (flow, seq);
                INSERT INTO events (flow, kind, at, payload) VALUES ('alpha', 'old', '2026-11-01T00:00:00.0000000Z', '{}');
                PRAGMA user_version = 1;
                """, old);
            await create.ExecuteNonQueryAsync();
        }

        await using var journal = await OpenEmptyAsync();
        var keyed = await journal.AppendEventAsync("alpha", new NewEvent("new", "{}", Key: "k", Tag: "t"));
        var again = await journal.AppendEventAsync("alpha", new NewEvent("new", "{}", Key: "k", Tag: "t"));

        var all = await journal.ReadAllEventsAsync();
        Assert.Equal(["old", "new"], all.Select(e => e.Kind));
        Assert.Equal([null, "t"], all.Select(e => e.Tag));
        Assert.Equal(keyed, again);
        await using var check = Connect();
        await check.OpenAsync();
        await using var version = new SqliteCommand("PRAGMA user_version", check);
        Assert.Equal(2L, await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_store_without_the_stream_says_so()
    {
        await using var journal = StepJournal.Open(new StepOnlyStore(await SqliteStepStore.OpenAsync(Path)));

        await Assert.ThrowsAsync<NotSupportedException>(() => journal.ReadAllEventsAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => journal.AppendEventAsync("alpha", new NewEvent("k", "{}")));
        Assert.Throws<NotSupportedException>(() => journal.Signal);
    }

    private SqliteConnection Connect() =>
        new(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());

    /// <summary>Forwards the step store of <see cref="SqliteStepStore"/> and hides its event feed.</summary>
    private sealed class StepOnlyStore(IStepStore inner) : IStepStore
    {
        public Task<StepClaim> ClaimAsync(string flow, string key, CancellationToken ct) => inner.ClaimAsync(flow, key, ct);

        public Task CompleteAsync(string flow, string key, int attempt, string result, CancellationToken ct) => inner.CompleteAsync(flow, key, attempt, result, ct);

        public Task FailAsync(string flow, string key, int attempt, string failure, CancellationToken ct) => inner.FailAsync(flow, key, attempt, failure, ct);

        public Task<bool> ResetAsync(string flow, string key, CancellationToken ct) => inner.ResetAsync(flow, key, ct);

        public Task<StepRecord?> FindAsync(string flow, string key, CancellationToken ct) => inner.FindAsync(flow, key, ct);

        public Task<IReadOnlyList<StepRecord>> UnfinishedAsync(string? flow, CancellationToken ct) => inner.UnfinishedAsync(flow, ct);

        public Task<long> AppendEventAsync(string flow, string kind, string payload, CancellationToken ct) => inner.AppendEventAsync(flow, kind, payload, ct);

        public Task<IReadOnlyList<JournalEvent>> ReadEventsAsync(string flow, long after, int limit, CancellationToken ct) => inner.ReadEventsAsync(flow, after, limit, ct);

        public Task<int> ForgetAsync(string flow, CancellationToken ct) => inner.ForgetAsync(flow, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
