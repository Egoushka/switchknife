using Npgsql;
using Switchknife.Journal.Tests;

namespace Switchknife.Journal.Postgres.Tests;

/// <summary>The event stream on <see cref="PgStepStore"/>; the shared body is <see cref="EventStreamContract"/>, the same one SQLite runs.</summary>
public sealed class PgEventStreamTests(PostgresFixture fixture) : EventStreamContract, IClassFixture<PostgresFixture>
{
    protected override string TagIndexName => "step_events_by_tag";

    protected override async Task<StepJournal> OpenEmptyAsync()
    {
        var postgres = fixture.Running();
        await postgres.ResetAsync();
        return StepJournal.Open(new PgStepStore(postgres.DataSource));
    }

    protected override async Task<long> CommittedCountAsync()
    {
        // A connection of its own, outside the store's data source: it sees committed rows only.
        await using var connection = new NpgsqlConnection(fixture.Running().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from step_events", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    protected override async Task<string> ExplainTagReadAsync(string tag)
    {
        await using var connection = new NpgsqlConnection(fixture.Running().ConnectionString);
        await connection.OpenAsync();
        await using (var analyze = new NpgsqlCommand("analyze step_events", connection))
            await analyze.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("explain " + PgStepStore.ReadByTagSql, connection);
        command.Parameters.AddWithValue("tag", tag);
        command.Parameters.AddWithValue("after", 0L);
        command.Parameters.AddWithValue("limit", 1000);
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(0));
        return string.Join("; ", plan);
    }

    [Fact]
    public async Task Two_stores_appending_the_same_key_at_once_store_one_event()
    {
        await using var first = await OpenEmptyAsync();
        await using var second = StepJournal.Open(new PgStepStore(fixture.Running().DataSource));

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            (i % 2 == 0 ? first : second).AppendEventAsync("alpha", new NewEvent("k", "{}", Key: "shared", Tag: "t"))));

        Assert.Single(results.Distinct());
        Assert.Equal(1, await CommittedCountAsync());
    }

    [Fact]
    public async Task The_schema_script_runs_twice_without_error_and_carries_the_event_stream_columns()
    {
        var postgres = fixture.Running();
        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        await using (var again = new NpgsqlCommand(PgStepStoreSchema.Sql, connection))
            await again.ExecuteNonQueryAsync();
        await using var columns = new NpgsqlCommand(
            "select column_name from information_schema.columns where table_name = 'step_events' and column_name in ('tag', 'key') order by 1", connection);
        await using var reader = await columns.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));

        Assert.Equal(["key", "tag"], names);
    }
}
