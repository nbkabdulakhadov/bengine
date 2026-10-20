namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Plans;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using Xunit.Abstractions;

/// <summary>
/// Tests execution plans: SQLite always, and PostgreSQL, MySQL, MariaDB, and SQL
/// Server against live servers when their <c>BENGINE_TEST_*</c> variables are
/// set (read-only statements against catalog views, so nothing is written).
/// </summary>
[Collection("live-postgres")]
public sealed class PlanReaderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Explains_sqlite_queries_as_a_tree()
    {
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, "Data Source=:memory:");
        await session.Query.ExecuteAllAsync("CREATE TABLE a (id INTEGER PRIMARY KEY, x TEXT); CREATE TABLE b (id INTEGER PRIMARY KEY, a_id INTEGER, y TEXT); CREATE INDEX ix_b_a ON b(a_id)");

        var plan = await PlanReader.ExplainAsync(session, "SELECT * FROM a JOIN b ON b.a_id = a.id WHERE a.x = 'q' ORDER BY b.y", analyze: false);

        var nodes = plan.Flatten().Select(n => n.Node).ToList();
        Assert.Contains(nodes, n => n.Operation.StartsWith("SCAN", StringComparison.Ordinal) && n.Object == "a");
        Assert.Contains(nodes, n => n.Operation.StartsWith("SEARCH", StringComparison.Ordinal) && n.Object == "b");
    }

    [Fact]
    public void Parses_postgres_json_with_actual_figures_and_flags_misestimates()
    {
        const string json = """
            [{"Plan": {"Node Type": "Hash Join", "Join Type": "Inner", "Total Cost": 120.5, "Plan Rows": 10, "Actual Rows": 5000, "Actual Total Time": 4.5, "Actual Loops": 1, "Hash Cond": "(b.a_id = a.id)",
              "Plans": [{"Node Type": "Seq Scan", "Relation Name": "b", "Alias": "b", "Total Cost": 80, "Plan Rows": 5000, "Actual Rows": 5000, "Actual Total Time": 1.5, "Actual Loops": 1},
                        {"Node Type": "Hash", "Total Cost": 20, "Plan Rows": 10, "Actual Rows": 10, "Actual Total Time": 0.5, "Actual Loops": 1,
                         "Plans": [{"Node Type": "Index Scan", "Relation Name": "a", "Alias": "a", "Index Name": "a_pkey", "Plan Rows": 10, "Actual Rows": 10, "Actual Total Time": 0.4, "Actual Loops": 1}]}]},
              "Planning Time": 0.2, "Execution Time": 5.1}]
            """;

        var plan = PlanReader.ParsePostgres(json, analyzed: true);
        plan.Annotate();

        var root = Assert.Single(plan.Roots);
        Assert.Equal("Hash Join (Inner)", root.Operation);
        Assert.Equal(2.5, root.SelfTimeMs!.Value, 3);
        Assert.Contains(root.Warnings, w => w.Contains("off by 500", StringComparison.Ordinal));
        Assert.Contains(root.Children[0].Warnings, w => w.StartsWith("Full scan of 5,000", StringComparison.Ordinal));
        Assert.Equal("a using a_pkey", root.Children[1].Children[0].Object);
        Assert.Equal(5.1, plan.ExecutionTimeMs);
    }

    [Fact]
    public void Flags_scans_that_read_many_rows_and_keep_few()
    {
        // A parallel scan in three loops that returns 123 rows and discards a million: the classic missing index.
        const string json = """
            [{"Plan": {"Node Type": "Gather Merge", "Total Cost": 20014.21, "Plan Rows": 314, "Actual Rows": 50, "Actual Total Time": 18.2, "Actual Loops": 1,
              "Plans": [{"Node Type": "Seq Scan", "Relation Name": "transactions", "Alias": "transactions", "Total Cost": 18972.33, "Plan Rows": 157,
                         "Actual Rows": 41, "Actual Total Time": 14.4, "Actual Loops": 3, "Filter": "(wallet_id = 7)", "Rows Removed by Filter": 333210}]},
              "Planning Time": 0.3, "Execution Time": 18.3}]
            """;

        var plan = PlanReader.ParsePostgres(json, analyzed: true);
        plan.Annotate();

        var scan = Assert.Single(Assert.Single(plan.Roots).Children);
        Assert.Contains(scan.Warnings, w => w.StartsWith("Full scan of 999,753 rows that keeps 123", StringComparison.Ordinal));
    }

    [Fact]
    public void Parses_mysql_tree_text()
    {
        const string text = """
            -> Limit: 5 row(s)  (actual time=26.5..26.5 rows=5 loops=1)
                -> Nested loop inner join  (cost=12624 rows=30025) (actual time=0.0856..23.9 rows=3610 loops=1)
                    -> Table scan on tbl  (cost=61.4 rows=371) (actual time=0.0116..0.128 rows=364 loops=1)
                    -> Single-row index lookup on sch using PRIMARY (id=tbl.schema_id)  (cost=0.25 rows=1) (actual time=72.7e-6..85.5e-6 rows=1 loops=364)
            """;

        var plan = PlanReader.ParseMySqlTree(text, analyzed: true);

        var join = Assert.Single(Assert.Single(plan.Roots).Children);
        Assert.Equal("Nested loop inner join", join.Operation);
        Assert.Equal(30025, join.EstimatedRows);
        Assert.Equal(2, join.Children.Count);
        Assert.Equal(("Table scan", "tbl"), (join.Children[0].Operation, join.Children[0].Object));
        Assert.Equal("sch using PRIMARY", join.Children[1].Object);
        Assert.Equal(364, join.Children[1].Loops);
    }

    [Fact]
    public async Task Explains_on_live_servers_when_configured()
    {
        var targets = new (string Variable, DatabaseProvider Provider, string Sql)[]
        {
            ("BENGINE_TEST_PG", DatabaseProvider.PostgreSql, "SELECT c.relname, count(*) FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid GROUP BY 1 ORDER BY 2 DESC LIMIT 5"),
            ("BENGINE_TEST_MYSQL", DatabaseProvider.MySql, "SELECT t.table_name, count(*) FROM information_schema.tables t JOIN information_schema.columns c ON c.table_name = t.table_name GROUP BY 1 ORDER BY 2 DESC LIMIT 5"),
            ("BENGINE_TEST_MARIADB", DatabaseProvider.MySql, "SELECT t.table_name, count(*) FROM information_schema.tables t JOIN information_schema.columns c ON c.table_name = t.table_name GROUP BY 1 ORDER BY 2 DESC LIMIT 5"),
            ("BENGINE_TEST_MSSQL", DatabaseProvider.SqlServer, "SELECT TOP 5 o.name, COUNT(*) AS n FROM sys.objects o JOIN sys.columns c ON c.object_id = o.object_id GROUP BY o.name ORDER BY n DESC"),
        };

        foreach (var (variable, provider, sql) in targets)
        {
            if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } spec)
            {
                continue;
            }

            var p = spec.Split(';');
            var server = new ServerProfile
            {
                Name = variable, Provider = provider, Host = p[0], Port = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                MaintenanceDatabase = p[2], Username = p[3], Password = p[4], SavePassword = true,
                Options = provider == DatabaseProvider.SqlServer ? "TrustServerCertificate=True" : null,
            };
            await using var session = await ObjectExplorer.OpenSessionAsync(server, null, null);
            foreach (var analyze in new[] { false, true })
            {
                var plan = await PlanReader.ExplainAsync(session, sql, analyze);
                var nodes = plan.Flatten().ToList();
                output.WriteLine($"{variable} analyze={analyze}: {nodes.Count} nodes; root {plan.Roots[0].Operation}");
                foreach (var (node, depth) in nodes.Take(6))
                {
                    output.WriteLine($"  {new string(' ', depth * 2)}{node.Operation} {node.Object} est={node.EstimatedRows} act={node.ActualRows} t={node.ActualTimeMs}");
                }

                Assert.True(nodes.Count >= 2, $"{variable}: plan has {nodes.Count} node(s)");
                if (analyze && variable != "BENGINE_TEST_MARIADB")
                {
                    Assert.Contains(nodes, n => n.Node.ActualRows is not null);
                }
            }
        }
    }
}
