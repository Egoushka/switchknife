using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Switchknife.Migrations.Tests;

public sealed class DatabaseMigratorTests
{
    private const string ConnectionString = "Host=localhost;Database=app";

    [Fact]
    public void Defaults_pick_resources_under_a_Migrations_folder_one_transaction_per_script_at_information()
    {
        var options = new MigrationOptions();

        Assert.True(options.ScriptFilter("App.Storage.Migrations.0001_init.sql"));
        Assert.False(options.ScriptFilter("App.Ui.app.css"));
        Assert.False(options.ScriptFilter("App.migrations.0001_init.sql"));
        Assert.Equal(MigrationTransaction.PerScript, options.Transaction);
        Assert.Equal(LogLevel.Information, options.DbUpInformationLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_connection_string_is_refused(string connectionString) =>
        Assert.Throws<ArgumentException>(() => new DatabaseMigrator(connectionString, typeof(DatabaseMigratorTests).Assembly, NullLogger.Instance));

    [Fact]
    public void Missing_arguments_are_refused()
    {
        var assembly = typeof(DatabaseMigratorTests).Assembly;

        Assert.Throws<ArgumentNullException>(() => new DatabaseMigrator(null!, assembly, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new DatabaseMigrator(ConnectionString, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new DatabaseMigrator(ConnectionString, assembly, null!));
    }

    [Fact]
    public void An_unknown_transaction_mode_is_refused_before_any_connection()
    {
        var options = new MigrationOptions { Transaction = (MigrationTransaction)7 };

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DatabaseMigrator(ConnectionString, typeof(DatabaseMigratorTests).Assembly, NullLogger.Instance, options));

        Assert.Equal("options", error.ParamName);
    }
}
