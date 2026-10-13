using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Switchknife.Migrations.Tests;

/// <summary>One PostgreSQL container for the class; each test migrates a database of its own. Skipped without Docker.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
            await _container.StartAsync();
        }
        catch (DockerUnavailableException e)
        {
            Unavailable = $"Docker is not available: {e.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>The connection string of a database that does not exist yet.</summary>
    public string NewDatabase()
    {
        Assert.SkipWhen(Unavailable is not null, Unavailable ?? "");
        return new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = $"m_{Guid.NewGuid():N}" }.ConnectionString;
    }
}

public sealed class PostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Good = "Switchknife.Migrations.Tests.Migrations.Good.";
    private const string Concurrent = "Switchknife.Migrations.Tests.Migrations.Concurrent.";

    private static MigrationOptions Set(string prefix, MigrationTransaction transaction = MigrationTransaction.PerScript) =>
        new() { ScriptFilter = name => name.StartsWith(prefix, StringComparison.Ordinal), Transaction = transaction };

    [Fact]
    public void A_missing_database_is_created_and_each_script_runs_once_in_name_order()
    {
        var database = postgres.NewDatabase();
        var logger = new CapturingLogger(LogLevel.Information);
        var migrator = new DatabaseMigrator(database, typeof(PostgresTests).Assembly, logger, Set(Good));

        var first = migrator.Run();
        var second = migrator.Run();

        Assert.Equal([Good + "0001_widgets.sql", Good + "0002_gadgets.sql"], first);
        Assert.Empty(second);
        Assert.Equal(2L, Scalar(database, "select count(*) from schemaversions"));
        Assert.Contains(logger.Entries, e => e.Message == $"Executing Database Server script '{Good}0002_gadgets.sql'");
        Assert.Equal(
            ["Database schema is up to date (2 script(s) applied).", "Database schema is up to date (0 script(s) applied)."],
            logger.Entries.Where(e => e.Message.StartsWith("Database schema", StringComparison.Ordinal)).Select(e => e.Message));
    }

    [Fact]
    public void A_failing_script_throws_naming_it_rolls_back_and_runs_again_once_fixed()
    {
        var database = postgres.NewDatabase();
        var migrator = new DatabaseMigrator(database, typeof(PostgresTests).Assembly, new CapturingLogger(), Set(Good));
        migrator.Run();
        Execute(database, "drop table gadgets, sprockets; delete from schemaversions where scriptname like '%0002_gadgets.sql'; create table sprockets (id int)");

        var error = Assert.Throws<InvalidOperationException>(migrator.Run);

        Assert.Equal($"Database migration failed on '{Good}0002_gadgets.sql'.", error.Message);
        Assert.Equal(PostgresErrorCodes.DuplicateTable, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(0L, Scalar(database, "select count(*) from pg_tables where tablename = 'gadgets'"));
        Assert.Equal(1L, Scalar(database, "select count(*) from schemaversions"));

        Execute(database, "drop table sprockets");
        Assert.Equal([Good + "0002_gadgets.sql"], migrator.Run());
    }

    [Fact]
    public void A_script_refused_inside_a_transaction_runs_without_one()
    {
        var database = postgres.NewDatabase();
        var assembly = typeof(PostgresTests).Assembly;
        new DatabaseMigrator(database, assembly, new CapturingLogger(), Set(Good)).Run();

        var refused = Assert.Throws<InvalidOperationException>(new DatabaseMigrator(database, assembly, new CapturingLogger(), Set(Concurrent)).Run);
        var applied = new DatabaseMigrator(database, assembly, new CapturingLogger(), Set(Concurrent, MigrationTransaction.None)).Run();

        Assert.Equal(PostgresErrorCodes.ActiveSqlTransaction, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
        Assert.Equal([Concurrent + "0001_index.sql"], applied);
        Assert.Equal(1L, Scalar(database, "select count(*) from pg_indexes where indexname = 'widgets_id'"));
    }

    private static object? Scalar(string database, string sql)
    {
        using var connection = new NpgsqlConnection(database);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar();
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new NpgsqlConnection(database);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
