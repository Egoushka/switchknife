using Microsoft.Extensions.Logging;

namespace Switchknife.Migrations;

/// <summary>How <see cref="DatabaseMigrator"/> picks, runs and logs scripts. The defaults suit most databases.</summary>
public sealed class MigrationOptions
{
    /// <summary>
    /// Which embedded resources are scripts, by manifest resource name. Default: names that contain <c>.Migrations.</c>,
    /// which is what an <c>EmbeddedResource</c> under a <c>Migrations</c> folder or link base is called.
    /// </summary>
    public Func<string, bool> ScriptFilter { get; init; } = static name => name.Contains(".Migrations.", StringComparison.Ordinal);

    /// <summary>Whether each script runs in a transaction of its own (the default) or in none.</summary>
    public MigrationTransaction Transaction { get; init; } = MigrationTransaction.PerScript;

    /// <summary>
    /// The level DbUp's information lines (each script it runs, the journal checks) are written at. Default
    /// <see cref="LogLevel.Information"/>; <see cref="LogLevel.Debug"/> keeps a routine start quiet. DbUp's trace, debug,
    /// warning and error lines keep their own level.
    /// </summary>
    public LogLevel DbUpInformationLevel { get; init; } = LogLevel.Information;
}

/// <summary>The transaction a script runs in.</summary>
public enum MigrationTransaction
{
    /// <summary>Each script in its own transaction: a failed script leaves nothing behind and is not journalled.</summary>
    PerScript,

    /// <summary>
    /// No transaction, for statements PostgreSQL refuses inside one (<c>create index concurrently</c>, TimescaleDB
    /// continuous aggregates). A script that fails part-way leaves its earlier statements applied, so write each one
    /// to be safe to run again (<c>if not exists</c>, <c>create or replace</c>).
    /// </summary>
    None,
}
