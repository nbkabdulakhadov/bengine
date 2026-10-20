namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Model;
using Bengine.Introspection.Sql;

/// <summary>
/// Tests the SQL editor engine: statement splitting, parameters, statement
/// classification, formatting, and catalog-driven completion.
/// </summary>
public sealed class SqlEditorEngineTests
{
    private static readonly DatabaseCatalog Shop = new(
        DatabaseProvider.PostgreSql,
        "shop",
        "public",
        ["public", "sales"],
        [
            new CatalogRelation("public", "users", TableKind.Table,
            [
                new CatalogColumn("id", "bigint", false, true), new CatalogColumn("email", "text", false, false),
                new CatalogColumn("created_at", "timestamp", true, false),
            ]),
            new CatalogRelation("public", "orders", TableKind.Table,
            [
                new CatalogColumn("id", "bigint", false, true), new CatalogColumn("user_id", "bigint", false, false),
                new CatalogColumn("total", "numeric(10,2)", true, false),
            ]),
            new CatalogRelation("public", "order_items", TableKind.Table,
            [
                new CatalogColumn("order_id", "bigint", false, true), new CatalogColumn("sku", "text", false, true),
            ]),
            new CatalogRelation("sales", "regions", TableKind.Table, [new CatalogColumn("code", "text", false, true)]),
            new CatalogRelation("public", "active_users", TableKind.View, [new CatalogColumn("id", "bigint", true, false)]),
        ],
        [
            new CatalogForeignKey("orders_user_fk", "public", "orders", ["user_id"], "public", "users", ["id"]),
            new CatalogForeignKey("items_order_fk", "public", "order_items", ["order_id"], "public", "orders", ["id"]),
        ],
        [new CatalogRoutine("public", "calc_total", IsProcedure: false)]);

    [Fact]
    public void Splits_on_semicolons_outside_strings_comments_and_dollar_bodies()
    {
        const string sql = """
            SELECT 'a;b' AS x; -- c;d
            CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql;
            /* ; */ SELECT 2
            """;

        var statements = SqlScript.Split(sql, DatabaseProvider.PostgreSql);

        Assert.Equal(3, statements.Count);
        Assert.Equal("SELECT 'a;b' AS x", statements[0].Text);
        Assert.StartsWith("-- c;d", statements[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("LANGUAGE plpgsql", statements[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("SELECT 2", statements[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_mysql_routine_bodies_and_honors_delimiter()
    {
        const string sql = """
            CREATE PROCEDURE p() BEGIN SELECT 1; IF 1 THEN SELECT 2; END IF; END;
            DELIMITER $$
            CREATE TRIGGER t BEFORE INSERT ON x FOR EACH ROW BEGIN SET NEW.a = 1; END$$
            DELIMITER ;
            SELECT 3;
            """;

        var statements = SqlScript.Split(sql, DatabaseProvider.MySql);

        Assert.Equal(3, statements.Count);
        Assert.EndsWith("END IF; END", statements[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("SET NEW.a = 1; END", statements[1].Text, StringComparison.Ordinal);
        Assert.Equal("SELECT 3", statements[2].Text);
    }

    [Fact]
    public void Splits_sql_server_batches_on_go()
    {
        const string sql = "CREATE PROCEDURE p AS SELECT 1; SELECT 2;\nGO\nEXEC p;\n";

        var statements = SqlScript.Split(sql, DatabaseProvider.SqlServer);

        Assert.Equal(["CREATE PROCEDURE p AS SELECT 1; SELECT 2;", "EXEC p"], statements.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void Finds_the_statement_at_the_caret()
    {
        const string sql = "SELECT 1;\nSELECT 2;\n\n\nSELECT 3";

        Assert.Equal("SELECT 1", SqlScript.StatementAt(sql, 3, DatabaseProvider.PostgreSql)!.Text);
        Assert.Equal("SELECT 2", SqlScript.StatementAt(sql, sql.IndexOf("2;", StringComparison.Ordinal) + 2, DatabaseProvider.PostgreSql)!.Text);
        Assert.Equal("SELECT 3", SqlScript.StatementAt(sql, sql.Length, DatabaseProvider.PostgreSql)!.Text);
    }

    [Fact]
    public void Finds_and_substitutes_parameters_but_not_casts_or_strings()
    {
        const string sql = "SELECT id::text, ':no' FROM t WHERE a = :name AND b = ${limit} AND c = ? AND data ? 'k'";

        var names = SqlParameters.Names(sql, DatabaseProvider.PostgreSql);
        var result = SqlParameters.Substitute(sql, new Dictionary<string, string> { ["name"] = "'bob'", ["limit"] = "10", ["?1"] = "3" }, DatabaseProvider.PostgreSql);

        Assert.Equal(["name", "limit", "?1"], names.ToArray());
        Assert.Equal("SELECT id::text, ':no' FROM t WHERE a = 'bob' AND b = 10 AND c = 3 AND data ? 'k'", result);
    }

    [Theory]
    [InlineData("SELECT * FROM t", SqlStatementKind.Query, false, false)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", SqlStatementKind.Query, false, false)]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t WHERE id IN (SELECT * FROM x)", SqlStatementKind.Dml, true, false)]
    [InlineData("DELETE FROM t", SqlStatementKind.Dml, true, true)]
    [InlineData("UPDATE t SET a = (SELECT 1 FROM u WHERE u.id = 2)", SqlStatementKind.Dml, true, true)]
    [InlineData("update t set a = 1 where id = 2", SqlStatementKind.Dml, true, false)]
    [InlineData("SELECT * INTO backup FROM t", SqlStatementKind.Query, true, false)]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t", SqlStatementKind.Query, true, false)]
    [InlineData("EXPLAIN SELECT 1", SqlStatementKind.Query, false, false)]
    [InlineData("TRUNCATE t", SqlStatementKind.Ddl, true, false)]
    [InlineData("BEGIN", SqlStatementKind.Transaction, false, false)]
    public void Classifies_statements(string sql, SqlStatementKind kind, bool canModify, bool unfiltered)
    {
        var info = SqlStatementInfo.Classify(sql, DatabaseProvider.PostgreSql);

        Assert.Equal((kind, canModify, unfiltered), (info.Kind, info.CanModify, info.IsUnfilteredDml));
    }

    [Fact]
    public void Formats_clauses_lists_conditions_and_subqueries()
    {
        const string sql = "select u.id, u.email, count(o.id) from users u left join orders o on o.user_id = u.id and o.total > 0 " +
                           "where u.id in (select user_id from orders where total > 10) group by u.id, u.email order by 3 desc";

        var formatted = SqlFormatter.FormatStatement(sql, DatabaseProvider.PostgreSql);

        Assert.Equal(
            """
            SELECT
                u.id,
                u.email,
                count(o.id)
            FROM users u
            LEFT JOIN orders o ON o.user_id = u.id
                AND o.total > 0
            WHERE u.id IN (
                SELECT user_id
                FROM orders
                WHERE total > 10
            )
            GROUP BY
                u.id,
                u.email
            ORDER BY 3 DESC
            """.ReplaceLineEndings("\n"),
            formatted);
    }

    [Fact]
    public void Formatting_keeps_literals_comments_and_separators()
    {
        const string sql = "select 'from x' -- keep\n, \"Mixed\" from t;\nselect 1";

        var formatted = SqlFormatter.Format(sql, DatabaseProvider.PostgreSql);

        Assert.Contains("'from x' -- keep", formatted, StringComparison.Ordinal);
        Assert.Contains("\"Mixed\"", formatted, StringComparison.Ordinal);
        Assert.Contains(";\nSELECT 1", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Completes_tables_after_from_with_schema_qualification_outside_the_default()
    {
        var result = Complete("SELECT * FROM |");

        Assert.Contains(result.Items, i => i.Kind == CompletionKind.Table && i.InsertText == "users");
        Assert.Contains(result.Items, i => i.InsertText == "sales.regions");
        Assert.Contains(result.Items, i => i.Kind == CompletionKind.View && i.Label == "active_users");
    }

    [Fact]
    public void Completes_columns_of_an_alias_declared_later_in_the_statement()
    {
        var result = Complete("SELECT u.| FROM users u");

        Assert.Equal(["created_at", "email", "id"], result.Items.Select(i => i.Label).Order().ToArray());
    }

    [Fact]
    public void Qualifies_ambiguous_columns_and_filters_by_prefix()
    {
        var result = Complete("SELECT i| FROM users u JOIN orders o ON o.user_id = u.id");

        Assert.Contains(result.Items, i => i.InsertText == "u.id");
        Assert.Contains(result.Items, i => i.InsertText == "o.id");
        Assert.DoesNotContain(result.Items, i => i.InsertText == "id");
        Assert.Equal(CompletionKind.Column, result.Items[0].Kind);
    }

    [Fact]
    public void Proposes_foreign_key_joins_and_conditions()
    {
        var joins = Complete("SELECT * FROM users u JOIN |");
        var conditions = Complete("SELECT * FROM users u JOIN orders o ON |");

        Assert.Contains(joins.Items, i => i.Kind == CompletionKind.Join && i.InsertText == "orders o ON o.user_id = u.id");
        Assert.Equal(CompletionKind.Condition, conditions.Items[0].Kind);
        Assert.Equal("o.user_id = u.id", conditions.Items[0].InsertText);
    }

    [Fact]
    public void Matches_word_starts_and_offers_keywords_after_a_table()
    {
        var byInitials = Complete("SELECT ui| FROM orders");
        var afterTable = Complete("SELECT * FROM orders o |");

        Assert.Equal("user_id", byInitials.Items[0].Label);
        Assert.Contains(afterTable.Items, i => i.Label == "WHERE");
        Assert.Contains(afterTable.Items, i => i.Label == "LEFT JOIN");
        Assert.DoesNotContain(afterTable.Items, i => i.Kind == CompletionKind.Table);
    }

    [Fact]
    public void Completes_insert_column_lists_and_schema_members()
    {
        var columns = Complete("INSERT INTO orders (|");
        var schema = Complete("SELECT * FROM sales.|");

        Assert.Equal(["id", "total", "user_id"], columns.Items.Select(i => i.Label).Order().ToArray());
        Assert.Equal("regions", Assert.Single(schema.Items).Label);
    }

    [Fact]
    public void Does_not_complete_inside_strings_or_comments()
    {
        Assert.Empty(Complete("SELECT 'us|'").Items);
        Assert.Empty(Complete("SELECT 1 -- fr|").Items);
    }

    [Fact]
    public void Derives_unique_aliases_from_table_names()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o" };

        Assert.Equal("oi", SqlCompletion.AliasFor("order_items", taken));
        Assert.Equal("o1", SqlCompletion.AliasFor("orders", taken));
        Assert.Equal("oi1", SqlCompletion.AliasFor("OrderItems", taken));
    }

    [Fact]
    public async Task Reads_a_sqlite_catalog_with_foreign_keys_and_runs_transactions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bengine_cat_{Guid.NewGuid():N}.db");
        try
        {
            await using (var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, $"Data Source={path}"))
            {
                await session.Query.ExecuteAsync("CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL)");
                await session.Query.ExecuteAsync("CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users, total REAL)");
                await session.Query.ExecuteAsync("CREATE VIEW v AS SELECT id FROM users");

                var catalog = await session.ReadCatalogAsync();
                Assert.Equal(["orders", "users", "v"], catalog.Relations.Select(r => r.Name).Order().ToArray());
                var key = Assert.Single(catalog.ForeignKeys);
                Assert.Equal(("orders", "users", "user_id", "id"), (key.Table, key.ReferencedTable, key.Columns[0], key.ReferencedColumns[0]));
                Assert.True(catalog.Find(null, "users")!.Column("id")!.IsPrimaryKey);

                await session.BeginTransactionAsync();
                await session.Writer.InsertAsync(new TableRef("users"), new Dictionary<string, object?> { ["email"] = "a@x" });
                await session.RollbackAsync();
                await session.BeginTransactionAsync();
                await session.Writer.InsertAsync(new TableRef("users"), new Dictionary<string, object?> { ["email"] = "b@x" });
                await session.CommitAsync();

                var rows = await session.Query.ExecuteAsync("SELECT email FROM users");
                Assert.Equal("b@x", Assert.Single(rows.Rows)[0]);
            }

            await using var readOnly = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, $"Data Source={path}", new SessionOptions { ReadOnly = true });
            await Assert.ThrowsAsync<IntrospectionException>(() => readOnly.Query.ExecuteAsync("DELETE FROM users"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_read_only_session_leaves_later_sessions_on_the_same_database_writable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bengine_ro_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            await using (var setup = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, connectionString))
            {
                await setup.Query.ExecuteAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT)");
            }

            await using (var readOnly = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, connectionString, new SessionOptions { ReadOnly = true }))
            {
                await readOnly.Query.ExecuteAsync("SELECT count(*) FROM notes");
            }

            // The read-only switch must not stay on a pooled connection that the next session gets.
            await using var writable = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, connectionString);
            var result = await writable.Query.ExecuteAsync("INSERT INTO notes (text) VALUES ('still writable')");
            Assert.Equal(1, result.RowsAffected);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Returns_every_result_set_of_a_batch()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, "Data Source=:memory:");

        var results = await session.Query.ExecuteAllAsync("SELECT 1 AS a; SELECT 2 AS b, 3 AS c");

        Assert.Equal(2, results.Count);
        Assert.Equal(["b", "c"], results[1].Columns.ToArray());
    }

    private static CompletionResult Complete(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return SqlCompletion.Complete(textWithCaret.Remove(caret, 1), caret, Shop);
    }
}

/// <summary>Tests of the SQL inspections.</summary>
public sealed class SqlInspectorTests
{
    private static readonly DatabaseCatalog Catalog = new(
        DatabaseProvider.PostgreSql, "shop", "public", ["public"],
        [
            new CatalogRelation("public", "users", TableKind.Table, [new CatalogColumn("id", "bigint", false, true), new CatalogColumn("email", "text", false, false)]),
            new CatalogRelation("public", "orders", TableKind.Table, [new CatalogColumn("id", "bigint", false, true), new CatalogColumn("user_id", "bigint", false, false), new CatalogColumn("total_x2", "numeric", true, false, IsGenerated: true)]),
        ],
        [], []);

    [Theory]
    [InlineData("SELECT * FROM userz", "unresolved-table")]
    [InlineData("SELECT u.emial FROM users u", "unresolved-column")]
    [InlineData("SELECT id FROM users u JOIN orders o ON o.user_id = u.id", "ambiguous-column")]
    [InlineData("DELETE FROM users", "missing-where")]
    [InlineData("SELECT * FROM users WHERE email = NULL", "null-comparison")]
    [InlineData("SELECT * FROM users WHERE 1 = 1", "constant-condition")]
    [InlineData("INSERT INTO users (id, email) VALUES (1)", "insert-values")]
    [InlineData("INSERT INTO orders (id, total_x2) VALUES (1, 2)", "generated-column")]
    [InlineData("SELECT id AS a, email AS a FROM users", "duplicate-alias")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM users", "unused-cte")]
    [InlineData("SELECT (1 FROM users", "parentheses")]
    [InlineData("SELECT 'abc FROM users", "unterminated")]
    public void Reports(string sql, string code)
    {
        var findings = SqlInspector.Inspect(sql, Catalog);

        Assert.Contains(findings, f => f.Code == code);
    }

    [Theory]
    [InlineData("SELECT u.id, o.user_id FROM users u JOIN orders o ON o.user_id = u.id WHERE u.email IS NULL")]
    [InlineData("UPDATE users SET email = NULL WHERE id = 1")]
    [InlineData("WITH recent AS (SELECT * FROM orders) SELECT * FROM recent r JOIN users u ON u.id = r.user_id")]
    [InlineData("SELECT id FROM users WHERE id IN (SELECT user_id FROM orders)")]
    [InlineData("SELECT * FROM generate_series(1, 10) g")]
    [InlineData("CREATE TABLE brand_new (id int)")]
    [InlineData("SELECT count(*) AS n FROM users")]
    [InlineData("INSERT INTO users (id, email) VALUES (1, 'a'), (2, 'b')")]
    [InlineData("SELECT * FROM other_db.public.t")]
    public void Stays_quiet_on_valid_sql(string sql)
    {
        var findings = SqlInspector.Inspect(sql, Catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void Flags_writes_on_read_only_connections()
    {
        Assert.Contains(SqlInspector.Inspect("UPDATE users SET email = 'x' WHERE id = 1", Catalog, readOnly: true), f => f.Code == "read-only");
    }
}
