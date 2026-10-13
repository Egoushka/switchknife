# Switchknife.Testing.Postgres

A PostgreSQL server in a [Testcontainers](https://dotnet.testcontainers.org/) container for integration tests: started once per run, migrated by your callback, and either emptied between tests or split into a database per test. No dependency on a test framework or on `Switchknife.Migrations`: a fixture of 5 lines wraps it in xUnit 2, xUnit v3 or anything else.

```csharp
var postgres = new PostgresDatabase("postgres:18-alpine", connectionString =>
    new DatabaseMigrator(connectionString, typeof(Program).Assembly, NullLogger.Instance).Run());
await postgres.StartAsync();       // starts the container, then runs the callback once
// postgres.ConnectionString, postgres.DataSource
await postgres.ResetAsync();       // between tests: empties every table, keeps the migration journal
await postgres.DisposeAsync();     // removes the container
```

The fixture, xUnit v3 (`IAsyncLifetime` returns `ValueTask`):

```csharp
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgresDatabase Postgres { get; } = new("postgres:18-alpine", Migrate);
    public async ValueTask InitializeAsync() => await Postgres.StartAsync();
    public ValueTask DisposeAsync() => Postgres.DisposeAsync();
}
```

xUnit 2 (`IAsyncLifetime` returns `Task`):

```csharp
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgresDatabase Postgres { get; } = new("postgres:18-alpine", Migrate);
    public Task InitializeAsync() => Postgres.StartAsync();
    public async Task DisposeAsync() => await Postgres.DisposeAsync();
}
```

Share it as a collection fixture (`[CollectionDefinition]` with `ICollectionFixture<PostgresFixture>`) so one container serves every test class, or as a class fixture for one class.

Members:

- `new PostgresDatabase(image, migrate?)`: pin the image by tag (`postgres:18-alpine`, `pgvector/pgvector:<tag>`), as the service runs it. `migrate` gets `ConnectionString` once, after the server accepts connections.
- `ConnectionString`, `DataSource`: the server's database (an `NpgsqlDataSource`, disposed with the server).
- `ResetAsync()`: one `truncate ... restart identity cascade` over every table of every schema, read from the catalog, so a new migration's table needs no list update. It keeps DbUp's journal (`schemaversions`) and tables that belong to an extension. Seeded rows a test needs back are yours to insert after.
- `CreateDatabaseAsync()`: an empty database with a new name, returned; the callback does not run on it (a test that builds an older schema step by step). `DropDatabaseAsync(name)` drops it, closing connections still open.
- `ConnectionStringFor(name)`: the same server, another database, which need not exist (a migrator that creates its database).
- `Unavailable`: see below.

Without Docker: outside CI (the `CI` variable is not `true`), `StartAsync` does not throw when no Docker engine answers; it sets `Unavailable` to the reason, and every member that needs the server throws `InvalidOperationException` with it. Under xUnit v3 skip on it, e.g. in a fixture method the tests call first:

```csharp
Assert.SkipWhen(Postgres.Unavailable is not null, Postgres.Unavailable ?? "");
```

Under xUnit 2 the tests fail with that message. In CI a missing engine fails `StartAsync`.

Limits:

- One container per `PostgresDatabase`, with Testcontainers' default user, password and database. Tests that share it and call `ResetAsync` must not run in parallel with each other (one xUnit collection).
- Native AOT: not supported and not needed (test code runs on the JIT); the package sets `IsAotCompatible` to false and has no case in the AOT smoke sample.
