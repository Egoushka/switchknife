using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Switchknife.Facts;
using Switchknife.Knowledge.Ideas;
using Switchknife.Testing.Postgres;

namespace Switchknife.Knowledge.Tests;

/// <summary>One server for the class; each test takes a database of its own. Skipped without Docker, outside CI.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgresDatabase _postgres = new("postgres:18-alpine");

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    /// <summary>A database with the facts schema applied, an idea store over it and a clock at <see cref="Harness.Day(int)"/> 0.</summary>
    public async Task<Harness> NewAsync()
    {
        Assert.SkipWhen(_postgres.Unavailable is not null, _postgres.Unavailable ?? "");
        var data = NpgsqlDataSource.Create(_postgres.ConnectionStringFor(await _postgres.CreateDatabaseAsync()));
        await using (var command = data.CreateCommand(FactsSchema.Sql))
        {
            await command.ExecuteNonQueryAsync();
        }

        var clock = new FakeTimeProvider(Harness.Day(0));
        var ideas = new IdeaStore(data, clock);
        await ideas.InitializeAsync();
        return new Harness(data, clock, ideas);
    }
}

public sealed class Harness(NpgsqlDataSource data, FakeTimeProvider clock, IdeaStore ideas) : IAsyncDisposable
{

    public IdeaStore Ideas { get; } = ideas;

    /// <summary>A store over the same namespace, to read what <see cref="Ideas"/> wrote.</summary>
    public FactsStore Facts { get; } = new(data, IdeaPredicates.Namespace, clock);

    /// <summary>Midnight UTC, <paramref name="n"/> days after 1 January 2026.</summary>
    public static DateTimeOffset Day(int n) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(n);

    public void At(int n) => clock.SetUtcNow(Day(n));

    public ValueTask DisposeAsync() => data.DisposeAsync();
}
