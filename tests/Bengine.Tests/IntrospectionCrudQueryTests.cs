namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Export;
using Bengine.Introspection.Model;

/// <summary>
/// End-to-end tests for the query console, row editing (CRUD), DDL rendering, and
/// export, against a real on-disk SQLite database created per test.
/// </summary>
public sealed class IntrospectionCrudQueryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bengine_crud_{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_path}";

    public async Task InitializeAsync()
    {
        await using var connection = DatabaseConnectionFactory.Create(DatabaseProvider.Sqlite, ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE products (
                id    INTEGER PRIMARY KEY,
                name  VARCHAR(100) NOT NULL,
                price INTEGER NOT NULL
            );
            INSERT INTO products (name, price) VALUES ('Widget', 10), ('Gadget', 20);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Inserts_updates_and_deletes_rows()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);
        var products = new TableRef("products");

        var inserted = await session.Writer.InsertAsync(products, new Dictionary<string, object?>
        {
            ["name"] = "Sprocket",
            ["price"] = 30,
        });
        Assert.Equal(1, inserted);

        var updated = await session.Writer.UpdateAsync(
            products,
            key: new Dictionary<string, object?> { ["name"] = "Sprocket" },
            values: new Dictionary<string, object?> { ["price"] = 35 });
        Assert.Equal(1, updated);

        var deleted = await session.Writer.DeleteAsync(
            products,
            key: new Dictionary<string, object?> { ["name"] = "Widget" });
        Assert.Equal(1, deleted);

        var page = await session.Data.QueryAsync(products, new DataQuery { IncludeTotalCount = true });
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Refuses_update_without_a_key()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        await Assert.ThrowsAsync<IntrospectionException>(() =>
            session.Writer.UpdateAsync(
                new TableRef("products"),
                key: new Dictionary<string, object?>(),
                values: new Dictionary<string, object?> { ["price"] = 0 }));
    }

    [Fact]
    public async Task Runs_a_select_in_the_query_console()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var result = await session.Query.ExecuteAsync("SELECT id, name, price FROM products ORDER BY id");

        Assert.True(result.IsQuery);
        Assert.Equal(["id", "name", "price"], result.Columns.ToArray());
        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public async Task Reports_affected_rows_for_a_non_query()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var result = await session.Query.ExecuteAsync("UPDATE products SET price = price + 1");

        Assert.False(result.IsQuery);
        Assert.Equal(2, result.RowsAffected);
    }

    [Fact]
    public async Task Surfaces_sql_errors_as_introspection_exceptions()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        await Assert.ThrowsAsync<IntrospectionException>(() =>
            session.Query.ExecuteAsync("SELECT * FROM does_not_exist"));
    }

    [Fact]
    public async Task Surfaces_a_table_filter_or_sort_the_server_rejects_as_an_introspection_exception()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);
        var products = new TableRef("products");

        // The total count's query fails first, then the page's (without the count).
        var counted = await Assert.ThrowsAsync<IntrospectionException>(() =>
            session.Data.QueryAsync(products, new DataQuery { WhereSql = "name = 'Widget' 'Gadget'", IncludeTotalCount = true }));
        Assert.Contains("syntax error", counted.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<IntrospectionException>(() =>
            session.Data.QueryAsync(products, new DataQuery { WhereSql = "name = 'Widget' 'Gadget'" }));
        await Assert.ThrowsAsync<IntrospectionException>(() =>
            session.Data.QueryAsync(products, new DataQuery { OrderBySql = "no_such_column" }));
    }

    [Fact]
    public async Task Renders_table_ddl_from_the_live_catalog()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);

        var table = await session.Introspector.IntrospectTableAsync(new TableRef("products"));
        var ddl = SchemaDdlRenderer.RenderTable(DatabaseProvider.Sqlite, table);

        Assert.Contains("CREATE TABLE", ddl);
        Assert.Contains("products", ddl);
        Assert.Contains("name", ddl);
    }

    [Fact]
    public async Task Exports_rows_as_csv_and_sql()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, ConnectionString);
        var page = await session.Data.QueryAsync(new TableRef("products"), new DataQuery { Sort = [new SortSpec("id")] });

        var csv = DataExporter.ToCsv(page.Columns, page.Rows);
        Assert.StartsWith("id,name,price", csv.TrimStart());
        Assert.Contains("Widget", csv);

        var inserts = DataExporter.ToSqlInserts(DatabaseProvider.Sqlite, new TableRef("products"), page.Columns, page.Rows);
        Assert.Contains("INSERT INTO \"products\"", inserts);
        Assert.Contains("'Widget'", inserts);
    }
}
