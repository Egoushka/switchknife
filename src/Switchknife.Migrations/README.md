# Switchknife.Migrations

Applies the SQL scripts embedded in an assembly to PostgreSQL with [DbUp](https://dbup.github.io/): in name order, each once, journalled in the database's `schemaversions` table. Run it on start, before anything reads the schema.

```xml
<!-- Embedded as <RootNamespace>.Migrations.<file>, which the default filter picks up. -->
<EmbeddedResource Include="../../db/migrations/*.sql" LinkBase="Migrations" />
```

```csharp
var migrator = new DatabaseMigrator(connectionString, typeof(Program).Assembly, logger);
migrator.Run();   // creates the database when missing, runs what is new, returns the names it ran
```

What `Run` does:

1. Creates the database when it does not exist. That check connects to the server's maintenance database without pooling, so starting many hosts against one server (tests) does not leave an idle connection per start.
2. Runs every embedded script whose resource name passes `ScriptFilter` and is not journalled yet, ordered by name, each in its own transaction.
3. Logs one line, `Database schema is up to date ({Applied} script(s) applied).`, and returns the applied names.

A failed script throws `InvalidOperationException`: the message names the script, the inner exception is the database error (`PostgresException`). With a transaction per script, the failed script leaves nothing behind and is not journalled; scripts before it stay applied.

Options (`MigrationOptions`, all optional):

| Option | Default | Use |
|---|---|---|
| `ScriptFilter` | name contains `.Migrations.` | Pick another folder, or migrate only up to a script (a test that builds an older schema). |
| `Transaction` | `PerScript` | `None` for statements PostgreSQL refuses inside a transaction: `create index concurrently`, TimescaleDB continuous aggregates. A script then fails part-way with its earlier statements applied, so write each one to run again safely (`if not exists`, `create or replace`). |
| `DbUpInformationLevel` | `Information` | The level of DbUp's information lines (each script run, journal checks). `Debug` keeps a routine start to the one summary line. |
| `JournalSchema` | `public` | A schema of its own for the journal table, so an app and a library (or two libraries) migrating one database do not share DbUp's default `schemaversions` table. Created with `CREATE SCHEMA IF NOT EXISTS` before any script runs, even one that would create that schema itself. |
| `JournalTable` | `schemaversions` | The journal table's name, in `JournalSchema`. |

Logging: DbUp's trace, debug, information, warning and error lines go to the `ILogger` you pass at the same level (information at `DbUpInformationLevel`). Each line is formatted by DbUp's composite format with the invariant culture and logged under the constant template `{Message}`, so it reads exactly as DbUp wrote it.

Native AOT: works. `samples/Switchknife.AotSmoke` publishes with Native AOT, warnings as errors, and migrates a real PostgreSQL database (create, two scripts, a second run with nothing to do, DbUp's lines through the logger). DbUp and Npgsql carry no trimming annotations of their own; the publish reports no trim or AOT warning for the paths this package uses. Scripts are read with `Assembly.GetManifestResourceStream`, which the AOT compiler keeps.

Limits:

- PostgreSQL only, scripts embedded in one assembly. Script variables and code scripts are DbUp features this package does not expose. The journal is always DbUp's table journal (schema and table name configurable through `JournalSchema` and `JournalTable`), not another `IJournal` implementation.
- No down migrations, no locking between processes: run one migrator per database at a time (on start of the single instance, or as a step before the others start).
- Synchronous, as DbUp is.
