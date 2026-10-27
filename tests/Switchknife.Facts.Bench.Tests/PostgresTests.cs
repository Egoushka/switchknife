using Npgsql;
using Switchknife.Facts.Bench;
using Switchknife.Testing.Postgres;

namespace Switchknife.Facts.Bench.Tests;

/// <summary>One server with pgvector for the class (the image the home server runs); each test takes a database of its own. Skipped without Docker, outside CI.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgresDatabase _postgres = new("pgvector/pgvector:0.8.7-pg18-trixie");

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    public async Task<NpgsqlDataSource> NewDatabaseAsync()
    {
        Assert.SkipWhen(_postgres.Unavailable is not null, _postgres.Unavailable ?? "");
        return NpgsqlDataSource.Create(_postgres.ConnectionStringFor(await _postgres.CreateDatabaseAsync()));
    }
}

public sealed class PostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_run_answers_every_as_of_query_as_the_oracle_does_and_keeps_history()
    {
        await using var data = await postgres.NewDatabaseAsync();

        var report = await Runner.RunAsync(data, new RunOptions(Subjects: 30, Facts: 900, Queries: 400, EpisodeSize: 5, VectorDimensions: 16, Seed: 3), fresh: false);

        Assert.Equal(0, report.AsOfWrong);
        Assert.Equal(0, report.AsOfOverlap);
        Assert.True(report.RetiredRows > 0, "Superseding must retire rows, not overwrite them.");
        Assert.NotNull(report.Nearest);
    }

    [Fact]
    public async Task The_adr_gates_hold_at_5000_facts()
    {
        // Latency on a shared machine is noisy (other containers start beside this one), so the thresholds must hold in the best of
        // three runs; a wrong answer fails any run, and a real regression of the write or read path misses the limit in all three.
        IReadOnlyList<string> failures = [];
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var data = await postgres.NewDatabaseAsync();

            var report = await Runner.RunAsync(data, new RunOptions(Subjects: 500, Facts: 5000, Queries: 1000, EpisodeSize: 5, VectorDimensions: 0, Seed: 5), fresh: false);

            Assert.Equal(0, report.AsOfWrong);
            Assert.Equal(0, report.AsOfOverlap);
            failures = Gates.Adr.Check(report);
            if (failures.Count == 0)
            {
                break;
            }
        }

        Assert.Empty(failures);
    }
}
