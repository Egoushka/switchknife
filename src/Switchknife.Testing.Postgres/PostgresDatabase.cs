using DotNet.Testcontainers.Builders;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Switchknife.Testing.Postgres;

/// <summary>
/// A PostgreSQL server in a container, for integration tests. Start it once per run (a collection or assembly fixture
/// wraps <see cref="StartAsync"/> and <see cref="DisposeAsync"/>); tests then share its database and empty it with
/// <see cref="ResetAsync"/>, or take a database of their own with <see cref="CreateDatabaseAsync"/> or
/// <see cref="ConnectionStringFor"/>.
/// </summary>
/// <remarks>
/// Without a Docker engine, outside CI (the <c>CI</c> variable is not <c>true</c>), <see cref="StartAsync"/> does not
/// throw: it sets <see cref="Unavailable"/>, which a test can skip on, and every member that needs the server throws
/// with that reason. In CI a missing engine fails the run.
/// </remarks>
public sealed class PostgresDatabase : IAsyncDisposable
{
    /// <summary>The table <see cref="ResetAsync"/> keeps: DbUp's journal, so a reset does not undo the migration record.</summary>
    public const string Journal = "schemaversions";

    private readonly string _image;
    private readonly Action<string>? _migrate;
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;
    private string? _connectionString;

    /// <summary>A server of <paramref name="image"/> (pin a tag, e.g. <c>postgres:18-alpine</c>), not started yet.</summary>
    /// <param name="image">The container image, by tag or digest.</param>
    /// <param name="migrate">Runs once after the server starts, with <see cref="ConnectionString"/>: apply the schema here.</param>
    public PostgresDatabase(string image, Action<string>? migrate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        _image = image;
        _migrate = migrate;
    }

    /// <summary>Why the server is not running (no Docker engine, outside CI); <see langword="null"/> when it is or has not started.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>The connection string of the server's database, the one <see cref="ResetAsync"/> empties.</summary>
    public string ConnectionString => _connectionString ?? throw NotRunning();

    /// <summary>A data source on <see cref="ConnectionString"/>, disposed with this object.</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw NotRunning();

    /// <summary>Starts the container, waits until it accepts connections and runs the migrate callback.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_container is not null || Unavailable is not null)
        {
            throw new InvalidOperationException("The server was started already.");
        }

        try
        {
            _container = new PostgreSqlBuilder(_image).Build();
            await _container.StartAsync(ct).ConfigureAwait(false);
        }
        catch (DockerUnavailableException e) when (Environment.GetEnvironmentVariable("CI") != "true")
        {
            Unavailable = $"Docker is not available: {e.Message}";
            return;
        }

        _connectionString = _container.GetConnectionString();
        _migrate?.Invoke(_connectionString);
        _dataSource = NpgsqlDataSource.Create(_connectionString);
    }

    /// <summary>The connection string of <paramref name="database"/> on this server, whether it exists or not.</summary>
    public string ConnectionStringFor(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;
    }

    /// <summary>Creates an empty database with a new unique name and returns that name; the migrate callback does not run on it.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken ct = default)
    {
        var name = $"db_{Guid.NewGuid():N}";
        await AdminAsync($"create database {Quote(name)}", ct).ConfigureAwait(false);
        return name;
    }

    /// <summary>Drops <paramref name="database"/> when it exists, closing the connections still open on it.</summary>
    public Task DropDatabaseAsync(string database, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return AdminAsync($"drop database if exists {Quote(database)} with (force)", ct);
    }

    /// <summary>
    /// Empties every table of the server's database in one <c>truncate ... restart identity cascade</c>: tables of every
    /// schema except the <see cref="Journal"/> and tables that belong to an extension. Rows a test needs back (a seeded
    /// default) are the caller's to insert after.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var connection = await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var tables = new List<string>();
            var list = new NpgsqlCommand(
                """
                select format('%I.%I', n.nspname, c.relname)
                from pg_class c join pg_namespace n on n.oid = c.relnamespace
                where c.relkind in ('r', 'p') and not c.relispartition
                  and n.nspname <> 'information_schema' and n.nspname not like 'pg\_%'
                  and c.relname <> @journal
                  and not exists (select 1 from pg_depend d where d.classid = 'pg_class'::regclass and d.objid = c.oid and d.deptype = 'e')
                order by 1
                """,
                connection);
            await using (list.ConfigureAwait(false))
            {
                list.Parameters.AddWithValue("journal", Journal);
                var reader = await list.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        tables.Add(reader.GetString(0));
                    }
                }
            }

            if (tables.Count == 0)
            {
                return;
            }

            var truncate = new NpgsqlCommand($"truncate {string.Join(", ", tables)} restart identity cascade", connection);
            await using (truncate.ConfigureAwait(false))
            {
                await truncate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Disposes the data source and removes the container.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Without pooling, so a dropped database leaves no idle connection behind in a pool.
    private async Task AdminAsync(string sql, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var command = new NpgsqlCommand(sql, connection);
            await using (command.ConfigureAwait(false))
            {
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private InvalidOperationException NotRunning() =>
        new(Unavailable ?? "The server is not running: call StartAsync first.");
}
