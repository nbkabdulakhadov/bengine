namespace Bengine.Introspection;

using System.Data;
using System.Data.Common;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Data;
using Bengine.Introspection.Mongo;
using Bengine.Introspection.Providers;

/// <summary>How a session is opened.</summary>
public sealed record SessionOptions
{
    /// <summary>Gets the default options.</summary>
    public static SessionOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the session must not change the database.
    /// PostgreSQL and MySQL enforce it on the server (read-only transactions) and
    /// SQLite through <c>query_only</c>; SQL Server and Oracle have no session
    /// switch, so callers must also refuse modifying statements themselves; the
    /// MongoDB console refuses commands that can write.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
/// An open, provider-independent working session against one live database. It
/// owns the connection and exposes the capabilities a database browser
/// needs over it — discovery (<see cref="Explorer"/>), schema reconstruction
/// (<see cref="Introspector"/>), paged data reads (<see cref="Data"/>), edits
/// (<see cref="Writer"/>), the query console (<see cref="Query"/>), and a bulk
/// metadata snapshot (<see cref="ReadCatalogAsync"/>) — each already wired to the
/// correct provider implementation. A session can hold an explicit transaction
/// (manual-commit mode); every component enlists in it. Open it with
/// <see cref="OpenAsync(DatabaseProvider, string, CancellationToken)"/> and
/// dispose it to close the connection (an open transaction is rolled back).
/// A MongoDB session has no SQL connection: its components speak to the
/// server's shared client (see <see cref="MongoConnection"/>), and it holds no
/// transactions.
/// </summary>
public sealed class DatabaseSession : IAsyncDisposable
{
    private readonly DbConnection? _connection;
    private readonly MongoConnection? _mongo;
    private DbTransaction? _transaction;

    private DatabaseSession(DbConnection connection, DatabaseProvider provider, SessionOptions options)
    {
        _connection = connection;
        Provider = provider;
        Options = options;
        var schemaProvider = CreateProvider(provider, connection, () => _transaction);
        Explorer = schemaProvider;
        Introspector = schemaProvider;
        Data = new RelationalDataReader(connection, provider) { TransactionSource = () => _transaction };
        Writer = new RelationalDataWriter(connection, provider) { TransactionSource = () => _transaction };
        Query = new RelationalQueryExecutor(connection, provider) { TransactionSource = () => _transaction };
        HookServerMessages();
    }

    private DatabaseSession(MongoConnection mongo, SessionOptions options)
    {
        _mongo = mongo;
        Provider = DatabaseProvider.MongoDb;
        Options = options;
        var schemaProvider = new MongoSchemaProvider(mongo);
        Explorer = schemaProvider;
        Introspector = schemaProvider;
        Data = new MongoDataReader(mongo);
        Writer = new MongoDataWriter(mongo);
        Query = new MongoQueryExecutor(mongo, options.ReadOnly);
    }

    /// <summary>Raised for informational server output: PostgreSQL notices, SQL Server <c>PRINT</c>, MySQL warnings.</summary>
    public event Action<string>? ServerMessage;

    /// <summary>Gets the provider this session speaks to.</summary>
    public DatabaseProvider Provider { get; }

    /// <summary>Gets the options the session was opened with.</summary>
    public SessionOptions Options { get; }

    /// <summary>Gets the discovery capability: databases, schemas, and relations.</summary>
    public IDatabaseExplorer Explorer { get; }

    /// <summary>Gets the introspection capability: reconstruct the schema AST.</summary>
    public IDatabaseIntrospector Introspector { get; }

    /// <summary>Gets the data-browsing capability: paged, filtered, sorted reads.</summary>
    public ITableDataReader Data { get; }

    /// <summary>Gets the data-editing capability: insert, update, and delete rows.</summary>
    public ITableDataWriter Writer { get; }

    /// <summary>Gets the query-console capability: run ad-hoc SQL.</summary>
    public IQueryExecutor Query { get; }

    /// <summary>Gets a value indicating whether an explicit transaction is open.</summary>
    public bool InTransaction => _transaction is not null;

    /// <summary>Gets the database the connection is attached to.</summary>
    public string Database => _mongo?.DatabaseName
        ?? (_connection is Oracle.ManagedDataAccess.Client.OracleConnection oracle ? oracle.ServiceName : _connection!.Database);

    /// <summary>Gets a value indicating whether the connection is still open.</summary>
    public bool IsOpen => _mongo is not null || _connection!.State == ConnectionState.Open;

    /// <summary>Gets the MongoDB connection of a MongoDB session, or <c>null</c>.</summary>
    public MongoConnection? Mongo => _mongo;

    /// <summary>Opens a session against a live database.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The driver-specific connection string.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An open session.</returns>
    /// <exception cref="IntrospectionException">The connection could not be opened.</exception>
    public static Task<DatabaseSession> OpenAsync(
        DatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default) =>
        OpenAsync(provider, connectionString, SessionOptions.Default, cancellationToken);

    /// <summary>Opens a session against a live database with options.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The driver-specific connection string.</param>
    /// <param name="options">The session options.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An open session.</returns>
    /// <exception cref="IntrospectionException">The connection could not be opened.</exception>
    public static async Task<DatabaseSession> OpenAsync(
        DatabaseProvider provider,
        string connectionString,
        SessionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (provider == DatabaseProvider.MongoDb)
        {
            return Open(await MongoConnection.OpenAsync(connectionString, cancellationToken: cancellationToken).ConfigureAwait(false), options);
        }

        var readOnlyStatement = options.ReadOnly ? ReadOnlyStatement(provider) : null;
        if (readOnlyStatement is not null)
        {
            connectionString = WithoutPooling(provider, connectionString);
        }

        var connection = await DatabaseConnectionFactory
            .OpenAsync(provider, connectionString, cancellationToken)
            .ConfigureAwait(false);

        if (readOnlyStatement is { } sql)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                Connection.OracleSupport.Prepare(command);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new IntrospectionException($"Could not make the session read-only. {ex.Message}", ex);
            }
        }

        return new DatabaseSession(connection, provider, options);
    }

    /// <summary>Opens a session over an open MongoDB connection.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="options">The session options.</param>
    /// <returns>The session.</returns>
    public static DatabaseSession Open(MongoConnection connection, SessionOptions options) => new(connection, options);

    /// <summary>Verifies that a connection string can connect, without keeping the session.</summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The connection string to test.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns><c>true</c> if a connection opened; otherwise <c>false</c>.</returns>
    public static async Task<bool> CanConnectAsync(
        DatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await OpenAsync(provider, connectionString, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IntrospectionException)
        {
            return false;
        }
    }

    /// <summary>Starts an explicit transaction; later work runs inside it until commit or rollback.</summary>
    /// <param name="isolation">The isolation level, or <see cref="IsolationLevel.Unspecified"/> for the server default.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the transaction is open.</returns>
    /// <exception cref="IntrospectionException">A transaction is already open, or the server refused.</exception>
    public async Task BeginTransactionAsync(IsolationLevel isolation = IsolationLevel.Unspecified, CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
        {
            throw new IntrospectionException("A transaction is already open.");
        }

        if (_connection is null)
        {
            throw new IntrospectionException("MongoDB consoles run each command on its own; transactions are not available.");
        }

        try
        {
            _transaction = await _connection.BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IntrospectionException($"Could not start a transaction. {ex.Message}", ex);
        }
    }

    /// <summary>Commits the open transaction.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    /// <exception cref="IntrospectionException">The commit failed; the transaction is gone either way.</exception>
    public Task CommitAsync(CancellationToken cancellationToken = default) => EndTransactionAsync(commit: true, cancellationToken);

    /// <summary>Rolls back the open transaction.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the transaction is rolled back.</returns>
    /// <exception cref="IntrospectionException">The rollback failed; the transaction is gone either way.</exception>
    public Task RollbackAsync(CancellationToken cancellationToken = default) => EndTransactionAsync(commit: false, cancellationToken);

    /// <summary>Reads a metadata snapshot of the database: relations, columns, foreign keys, and routines.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="IntrospectionException">A catalog query failed.</exception>
    public Task<DatabaseCatalog> ReadCatalogAsync(CancellationToken cancellationToken = default) => _mongo is not null
        ? MongoCatalog.ReadAsync(_mongo, cancellationToken)
        : CatalogReader.ReadAsync(_connection!, Provider, _transaction, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            try
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The connection is going away; the server rolls back regardless.
            }

            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        // A MongoDB client is shared by the server's sessions and stays open.
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EndTransactionAsync(bool commit, CancellationToken cancellationToken)
    {
        if (_transaction is not { } transaction)
        {
            throw new IntrospectionException("No transaction is open.");
        }

        try
        {
            if (commit)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IntrospectionException($"Could not {(commit ? "commit" : "roll back")} the transaction. {ex.Message}", ex);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    private void HookServerMessages()
    {
        switch (_connection)
        {
            case Npgsql.NpgsqlConnection pg:
                pg.Notice += (_, e) => ServerMessage?.Invoke($"{e.Notice.Severity}: {e.Notice.MessageText}");
                break;
            case Microsoft.Data.SqlClient.SqlConnection sql:
                sql.FireInfoMessageEventOnUserErrors = false;
                sql.InfoMessage += (_, e) => ServerMessage?.Invoke(e.Message);
                break;
            case Oracle.ManagedDataAccess.Client.OracleConnection oracle:
                oracle.InfoMessage += (_, e) => ServerMessage?.Invoke(e.Message);
                break;
            case MySqlConnector.MySqlConnection my:
                my.InfoMessage += (_, e) =>
                {
                    foreach (var error in e.Errors)
                    {
                        ServerMessage?.Invoke($"{error.Level}: {error.Message}");
                    }
                };
                break;
        }
    }

    private static string? ReadOnlyStatement(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSql => "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY",
        DatabaseProvider.MySql => "SET SESSION TRANSACTION READ ONLY",
        DatabaseProvider.Sqlite => "PRAGMA query_only = ON",
        _ => null,
    };

    /// <summary>
    /// Takes the connection of a read-only session out of the driver's pool. The read-only switch is session
    /// state, and a pooled connection keeps it after the session closes: SQLite always does, Npgsql and
    /// MySqlConnector when their reset on close is turned off. A later writable session handed that connection
    /// would be read-only too.
    /// </summary>
    /// <param name="provider">The target provider.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The connection string with pooling off, or unchanged when it cannot be parsed (opening reports why).</returns>
    private static string WithoutPooling(DatabaseProvider provider, string connectionString)
    {
        try
        {
            return provider switch
            {
                DatabaseProvider.PostgreSql => new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString,
                DatabaseProvider.MySql => new MySqlConnector.MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString,
                DatabaseProvider.Sqlite => new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString,
                _ => connectionString,
            };
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }

    private static SchemaProviderBase CreateProvider(DatabaseProvider provider, DbConnection connection, Func<DbTransaction?> transaction) => provider switch
    {
        DatabaseProvider.PostgreSql => new PostgreSqlSchemaProvider(connection) { TransactionSource = transaction },
        DatabaseProvider.SqlServer => new SqlServerSchemaProvider(connection) { TransactionSource = transaction },
        DatabaseProvider.MySql => new MySqlSchemaProvider(connection) { TransactionSource = transaction },
        DatabaseProvider.Sqlite => new SqliteSchemaProvider(connection) { TransactionSource = transaction },
        DatabaseProvider.Oracle => new OracleSchemaProvider(connection) { TransactionSource = transaction },
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider."),
    };
}
