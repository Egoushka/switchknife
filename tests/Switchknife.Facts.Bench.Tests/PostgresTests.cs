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
    public async Task The_schema_applies_twice_and_refuses_two_live_values_for_one_slot_at_the_same_time()
    {
        await using var data = await postgres.NewDatabaseAsync();
        var store = new FactsStore(data);
        var workload = new Workload(2, 20, seed: 1);
        await store.CreateSchemaAsync(fresh: false);
        await store.CreateSchemaAsync(fresh: false);
        await store.SeedAsync(workload, Workload.Origin);
        var subject = workload.Subjects[0];

        const string Episode =
            """
            insert into facts.episodes (id, namespace, source_kind, source_ref, content_hash, occurred_at, recorded_at, status)
            values ('00000000-0000-0000-0000-000000000001', 'bench', 'k', 'r', 'h', now(), now(), 'done')
            """;
        const string Fact =
            """
            insert into facts.facts (id, namespace, subject_id, predicate, object_value, text, valid, recorded_at, slot, fingerprint, episode_id)
            values (gen_random_uuid(), 'bench', $1, 'lives_in', 'x', 'lives in x', tstzrange('2026-01-01', null, '[)'), now(), 'slot-1', $2, '00000000-0000-0000-0000-000000000001')
            """;
        await Execute(data, Episode);
        await Execute(data, Fact, subject, "a");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(data, Fact, subject, "b"));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, error.SqlState);
    }

    private static async Task Execute(NpgsqlDataSource data, string sql, params object[] values)
    {
        await using var command = data.CreateCommand(sql);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync();
    }
}
