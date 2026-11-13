using System.Reflection;
using DbUp;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Switchknife.Migrations;

/// <summary>
/// Applies the SQL scripts embedded in an assembly to a PostgreSQL database with DbUp: in name order, each once,
/// journalled in the database's <c>schemaversions</c> table. Creates the database first when it does not exist.
/// </summary>
public sealed partial class DatabaseMigrator
{
    private readonly string _connectionString;
    private readonly Assembly _scripts;
    private readonly ILogger _logger;
    private readonly MigrationOptions _options;

    /// <summary>Creates a migrator; nothing touches the database until <see cref="Run"/>.</summary>
    /// <param name="connectionString">The Npgsql connection string of the database to migrate.</param>
    /// <param name="scripts">The assembly the scripts are embedded in.</param>
    /// <param name="logger">Receives DbUp's log and one summary line.</param>
    /// <param name="options">Which resources are scripts, the transaction mode, the journal table and DbUp's information level; defaults when null.</param>
    public DatabaseMigrator(string connectionString, Assembly scripts, ILogger logger, MigrationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = connectionString;
        _scripts = scripts;
        _logger = logger;
        _options = options ?? new MigrationOptions();
        if (!Enum.IsDefined(_options.Transaction))
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.Transaction, "Unknown transaction mode.");
        }
        if (string.IsNullOrWhiteSpace(_options.JournalSchema))
        {
            throw new ArgumentException("JournalSchema must not be blank.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(_options.JournalTable))
        {
            throw new ArgumentException("JournalTable must not be blank.", nameof(options));
        }
    }

    /// <summary>
    /// Creates the database when it is missing, then runs every script not yet journalled.
    /// </summary>
    /// <returns>The names of the scripts this run applied, in order; empty when the schema was already up to date.</returns>
    /// <exception cref="InvalidOperationException">A script failed. The message names it; the inner exception is the database error.</exception>
    public IReadOnlyList<string> Run()
    {
        // The check connects to the server's maintenance database; unpooled, so each start does not leave an idle
        // connection behind in a pool of its own (many test hosts on one server add up until it refuses clients).
        var log = new UpgradeLog(_logger, _options.DbUpInformationLevel);
        EnsureDatabase.For.PostgresqlDatabase(
            new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString, log);

        if (!string.Equals(_options.JournalSchema, "public", StringComparison.Ordinal))
        {
            EnsureJournalSchema();
        }

        var builder = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(_scripts, _options.ScriptFilter)
            .JournalToPostgresqlTable(_options.JournalSchema, _options.JournalTable);
        builder = _options.Transaction == MigrationTransaction.None ? builder.WithoutTransaction() : builder.WithTransactionPerScript();

        var result = builder.LogTo(log).Build().PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed on '{result.ErrorScript?.Name ?? "(unknown script)"}'.", result.Error);
        }

        var applied = result.Scripts.Select(s => s.Name).ToArray();
        UpToDate(_logger, applied.Length);
        return applied;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is up to date ({Applied} script(s) applied).")]
    private static partial void UpToDate(ILogger logger, int applied);

    /// <summary>
    /// Creates <see cref="MigrationOptions.JournalSchema"/> when it does not exist yet, before DbUp looks for its journal
    /// table there: DbUp's Postgresql journal creates the table but not the schema, and a script that would create the
    /// schema itself has not run yet on a first migration.
    /// </summary>
    private void EnsureJournalSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(_options.JournalSchema)}", connection);
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
