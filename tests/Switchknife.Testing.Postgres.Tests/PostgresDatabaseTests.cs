using Npgsql;

namespace Switchknife.Testing.Postgres.Tests;

/// <summary>The xUnit v3 wrapper the README shows, migrated by a callback that counts its runs.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgresFixture() => Postgres = new PostgresDatabase("postgres:18-alpine", Migrate);

    public PostgresDatabase Postgres { get; }

    public List<string> Migrated { get; } = [];

    public async ValueTask InitializeAsync() => await Postgres.StartAsync();

    public ValueTask DisposeAsync() => Postgres.DisposeAsync();

    /// <summary>The running server; skips the test when Docker is missing outside CI.</summary>
    public PostgresDatabase Running()
    {
        Assert.SkipWhen(Postgres.Unavailable is not null, Postgres.Unavailable ?? "");
        return Postgres;
    }

    private void Migrate(string connectionString)
    {
        Migrated.Add(connectionString);
        PostgresDatabaseTests.Execute(connectionString, """
            create table schemaversions (scriptname text primary key);
            insert into schemaversions values ('0001_widgets.sql');
            create table widgets (id serial primary key, name text not null);
            create schema stock;
            create table stock.parts (id serial primary key, widget int not null references widgets (id));
            """);
    }
}

public sealed class PostgresDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void Migrate_runs_once_with_the_server_connection_string()
    {
        var postgres = fixture.Running();

        Assert.Equal([postgres.ConnectionString], fixture.Migrated);
        Assert.Equal(1L, Scalar(postgres.ConnectionString, "select count(*) from schemaversions"));
    }

    [Fact]
    public async Task Reset_empties_every_table_of_every_schema_restarts_identities_and_keeps_the_journal()
    {
        var postgres = fixture.Running();
        Execute(postgres.ConnectionString, "insert into widgets (name) values ('a'), ('b'); insert into stock.parts (widget) values (1), (2)");

        await postgres.ResetAsync();
        Execute(postgres.ConnectionString, "insert into widgets (name) values ('c')");

        Assert.Equal(1L, Scalar(postgres.ConnectionString, "select count(*) from widgets"));
        Assert.Equal(1, Scalar(postgres.ConnectionString, "select id from widgets"));
        Assert.Equal(0L, Scalar(postgres.ConnectionString, "select count(*) from stock.parts"));
        Assert.Equal(1L, Scalar(postgres.ConnectionString, "select count(*) from schemaversions"));
    }

    [Fact]
    public async Task The_data_source_reaches_the_server_database()
    {
        var postgres = fixture.Running();

        await using var command = postgres.DataSource.CreateCommand("select current_database()");

        Assert.Equal(new NpgsqlConnectionStringBuilder(postgres.ConnectionString).Database, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_created_database_is_empty_and_its_own_and_drops_with_connections_open()
    {
        var postgres = fixture.Running();

        var name = await postgres.CreateDatabaseAsync();
        var other = await postgres.CreateDatabaseAsync();
        await using var open = new NpgsqlConnection(postgres.ConnectionStringFor(name));
        await open.OpenAsync();
        await postgres.DropDatabaseAsync(name);
        await postgres.DropDatabaseAsync(name);

        Assert.NotEqual(name, other);
        Assert.Equal(0L, Scalar(postgres.ConnectionStringFor(other), "select count(*) from pg_tables where schemaname = 'public'"));
        Assert.Equal(0L, Scalar(postgres.ConnectionString, $"select count(*) from pg_database where datname = '{name}'"));
    }

    [Fact]
    public void A_named_database_need_not_exist()
    {
        var postgres = fixture.Running();

        var connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringFor("not_yet"));

        Assert.Equal("not_yet", connectionString.Database);
        Assert.Equal(new NpgsqlConnectionStringBuilder(postgres.ConnectionString).Host, connectionString.Host);
    }

    internal static object? Scalar(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar();
    }

    internal static void Execute(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}

public sealed class NotStartedTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_image_is_refused(string image) =>
        Assert.Throws<ArgumentException>(() => new PostgresDatabase(image));

    [Fact]
    public async Task Members_that_need_the_server_say_to_start_it_first()
    {
        await using var postgres = new PostgresDatabase("postgres:18-alpine");

        var error = Assert.Throws<InvalidOperationException>(() => postgres.ConnectionString);
        Assert.Throws<InvalidOperationException>(() => postgres.DataSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => postgres.ResetAsync());
        Assert.Contains("StartAsync", error.Message, StringComparison.Ordinal);
        Assert.Null(postgres.Unavailable);
    }
}
