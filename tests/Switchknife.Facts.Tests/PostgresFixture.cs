using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Switchknife.Testing.Postgres;

namespace Switchknife.Facts.Tests;

/// <summary>One server for the class; each test takes a database of its own. Skipped without Docker, outside CI.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgresDatabase _postgres = new("postgres:18-alpine");

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    /// <summary>The connection string of a new empty database.</summary>
    public async Task<string> NewDatabaseNameAsync()
    {
        Assert.SkipWhen(_postgres.Unavailable is not null, _postgres.Unavailable ?? "");
        return await _postgres.CreateDatabaseAsync();
    }

    public string ConnectionStringFor(string database) => _postgres.ConnectionStringFor(database);

    /// <summary>An empty database, not migrated.</summary>
    public async Task<NpgsqlDataSource> NewEmptyAsync() => NpgsqlDataSource.Create(ConnectionStringFor(await NewDatabaseNameAsync()));

    /// <summary>A database with the facts schema applied, a store over it in namespace <c>test</c> and a clock set to <see cref="Day(int)"/> 0.</summary>
    public async Task<Harness> NewAsync(string @namespace = "test")
    {
        var data = await NewEmptyAsync();
        await using (var command = data.CreateCommand(FactsSchema.Sql))
        {
            await command.ExecuteNonQueryAsync();
        }

        var clock = new FakeTimeProvider(Harness.Day(0));
        return new Harness(data, clock, new FactsStore(data, @namespace, clock));
    }
}

public sealed class Harness(NpgsqlDataSource data, FakeTimeProvider clock, FactsStore store) : IAsyncDisposable
{
    public NpgsqlDataSource Data { get; } = data;

    public FakeTimeProvider Clock { get; } = clock;

    public FactsStore Store { get; } = store;

    /// <summary>Midnight UTC, <paramref name="n"/> days after 1 January 2026.</summary>
    public static DateTimeOffset Day(int n) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(n);

    public ValueTask DisposeAsync() => Data.DisposeAsync();

    /// <summary>Moves the store's clock to <paramref name="n"/> days after 1 January 2026.</summary>
    public void At(int n) => Clock.SetUtcNow(Day(n));

    public async Task<long> CountAsync(string table)
    {
        await using var command = Data.CreateCommand($"select count(*) from facts.{table}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<IngestResult> IngestAsync(string source, params NewFact[] facts) =>
        await Store.IngestAsync(new NewEpisode("test", source, source), facts);

    public static NewFact Lives(Guid subject, string city, int fromDay) => new(subject, "lives_in", city, Day(fromDay));
}
