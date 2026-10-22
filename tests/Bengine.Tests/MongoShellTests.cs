namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Model;
using Bengine.Introspection.Mongo;
using Bengine.Introspection.Plans;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Introspection.Tree;
using MongoDB.Bson;

/// <summary>The MongoDB console language, values, connection URLs, completion, plans, and explorer commands, offline.</summary>
public sealed class MongoShellTests
{
    [Fact]
    public void Splits_a_script_into_commands()
    {
        const string script = """
            // users first
            db.users.find({ name: /a\/b/i })
              .sort({ name: 1 })
              .limit(5)
            db.users.insertOne({
              name: "x; y",   // not a separator
              tags: ["a", "b"]
            }); db.stats()
            show collections
            /* done */
            """;
        var statements = SqlScript.Split(script, DatabaseProvider.MongoDb);
        Assert.Equal(4, statements.Count);
        Assert.StartsWith("db.users.find", statements[0].Text, StringComparison.Ordinal);
        Assert.EndsWith(".limit(5)", statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("\"x; y\"", statements[1].Text, StringComparison.Ordinal);
        Assert.Equal("db.stats()", statements[2].Text);
        Assert.Equal("show collections", statements[3].Text);
        Assert.Equal("db.stats()", SqlScript.StatementAt(script, script.IndexOf("stats", StringComparison.Ordinal), DatabaseProvider.MongoDb)!.Text);
    }

    [Fact]
    public void Parses_commands()
    {
        var find = MongoShell.Parse("db.users.find({age: {$gt: 30}, _id: ObjectId('65a1b2c3d4e5f60718293a4b')}, {name: 1}).sort({name: -1}).limit(5);");
        Assert.Equal(ShellTarget.Collection, find.Target);
        Assert.Equal("users", find.Collection);
        Assert.Equal(["find", "sort", "limit"], find.Calls.Select(c => c.Name));
        Assert.Equal(30, find.Method!.Document(0)["age"]["$gt"].AsInt32);
        Assert.Equal(BsonType.ObjectId, find.Method.Document(0)["_id"].BsonType);
        Assert.Equal(5, find.Calls[2].Number(0));

        Assert.Equal("my-coll", MongoShell.Parse("db.getCollection(\"my-coll\").countDocuments()").Collection);
        Assert.Equal("system.users", MongoShell.Parse("db.system.users.find()").Collection);
        Assert.Equal("users", MongoShell.Parse("db['users'].find()").Collection);
        var sibling = MongoShell.Parse("db.getSiblingDB('admin').getCollection('x').find()");
        Assert.Equal(("admin", "x"), (sibling.Database, sibling.Collection));
        Assert.Equal("find", MongoShell.Parse("db.users").Method!.Name);
        Assert.Equal(ShellTarget.Database, MongoShell.Parse("db.stats()").Target);
        Assert.Equal("dbs", MongoShell.Parse("show dbs").Word);
        Assert.Equal(ShellTarget.Command, MongoShell.Parse("{ ping: 1 }").Target);
        Assert.Equal(2, MongoShell.Parse("db.c.aggregate([{$match: {a: 1}}, {$limit: 3},])").Method!.Array(0).Count);

        Assert.Throws<Bengine.Introspection.IntrospectionException>(() => MongoShell.Parse("SELECT 1"));
        Assert.Throws<Bengine.Introspection.IntrospectionException>(() => MongoShell.Parse("db.users.find().length"));
        Assert.Throws<Bengine.Introspection.IntrospectionException>(() => MongoShell.Parse("db.users.find({a: })"));
    }

    [Fact]
    public void Classifies_commands_for_the_console_checks()
    {
        SqlStatementInfo Classify(string text) => SqlStatementInfo.Classify(text, DatabaseProvider.MongoDb);

        Assert.False(Classify("db.users.find({})").CanModify);
        Assert.False(Classify("show dbs").CanModify);
        Assert.False(Classify("db.users.aggregate([{$match: {}}])").CanModify);
        Assert.True(Classify("db.users.aggregate([{$match: {}}, {$out: 'copy'}])").CanModify);
        Assert.True(Classify("db.users.insertOne({a: 1})").CanModify);
        Assert.Equal(SqlStatementKind.Dml, Classify("db.users.updateOne({a: 1}, {$set: {b: 2}})").Kind);
        Assert.True(Classify("db.users.deleteMany({})").IsUnfilteredDml);
        Assert.False(Classify("db.users.deleteMany({a: 1})").IsUnfilteredDml);
        Assert.True(Classify("db.users.updateMany({}, {$set: {b: 1}})").IsUnfilteredDml);
        Assert.False(Classify("db.users.explain().deleteMany({})").CanModify);
        Assert.Equal(SqlStatementKind.Ddl, Classify("db.users.createIndex({a: 1})").Kind);
        Assert.Equal(SqlStatementKind.Ddl, Classify("db.runCommand({drop: 'users'})").Kind);
        Assert.False(Classify("db.runCommand({ping: 1})").CanModify);
        Assert.False(Classify("{ listCollections: 1 }").CanModify);
        Assert.Empty(SqlParameters.Names("db.users.find({a: '$x', b: ':y'})", DatabaseProvider.MongoDb));
    }

    [Fact]
    public void Converts_values_between_text_cells_and_bson()
    {
        var document = new BsonDocument
        {
            { "_id", ObjectId.Parse("65a1b2c3d4e5f60718293a4b") },
            { "name", "Ann \"A\"" },
            { "age", 41 },
            { "big", 5_000_000_000L },
            { "ratio", 5.0 },
            { "price", new BsonDecimal128(12.50m) },
            { "born", new BsonDateTime(new DateTime(1984, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc)) },
            { "tags", new BsonArray { "a", 1, BsonNull.Value } },
            { "address", new BsonDocument { { "city", "Tashkent" }, { "zip code", "100000" } } },
            { "pattern", new BsonRegularExpression("^a", "i") },
            { "id", new BsonBinaryData(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), GuidRepresentation.Standard) },
        };
        var shell = MongoValues.Shell(document);
        Assert.Contains("_id: ObjectId(\"65a1b2c3d4e5f60718293a4b\")", shell, StringComparison.Ordinal);
        Assert.Contains("ratio: 5.0", shell, StringComparison.Ordinal);
        Assert.Contains("\"zip code\": \"100000\"", shell, StringComparison.Ordinal);
        Assert.Equal(document, MongoValues.ParseValue(shell));

        Assert.Equal(41, MongoValues.ToCell(document["age"]));
        Assert.Equal(12.50m, MongoValues.ToCell(document["price"]));
        Assert.IsType<BsonCell>(MongoValues.ToCell(document["_id"]));
        Assert.Equal(document["_id"], MongoValues.ToBson(MongoValues.ToCell(document["_id"])));
        Assert.Equal(document["address"], MongoValues.ToBson(MongoValues.ToCell(document["address"])));

        Assert.Equal(new BsonInt32(7), MongoValues.FromText("7", "int"));
        Assert.Equal(new BsonInt64(5_000_000_000L), MongoValues.FromText("5000000000", "int"));
        Assert.Equal(new BsonString("007"), MongoValues.FromText("007", "string"));
        Assert.Equal(new BsonDouble(2.5), MongoValues.FromText("2.5", "double"));
        Assert.Equal(BsonBoolean.True, MongoValues.FromText("true", "bool"));
        Assert.Equal(document["_id"], MongoValues.FromText("65a1b2c3d4e5f60718293a4b", "objectId"));
        Assert.Equal(document["born"], MongoValues.FromText("1984-05-06 07:08:09.123", "date"));
        Assert.Equal(new BsonDocument("a", new BsonArray { 1, 2 }), MongoValues.FromText("{a: [1, 2]}", "object"));
        Assert.Equal(new BsonInt32(3), MongoValues.FromText("3", "mixed"));
        Assert.Equal(new BsonString("plain text"), MongoValues.FromText("plain text", null));
    }

    [Fact]
    public void Builds_connection_urls()
    {
        var server = new ServerProfile
        {
            Name = "m", Provider = DatabaseProvider.MongoDb, Host = "db.local", Port = 27018, MaintenanceDatabase = "admin",
            Username = "app user", Options = "readPreference=secondaryPreferred\nmaxPoolSize=5",
        };
        var url = new MongoDB.Driver.MongoUrl(ConnectionStringFactory.Build(server, "shop", "p@ss:word"));
        Assert.Equal("db.local", url.Server.Host);
        Assert.Equal(27018, url.Server.Port);
        Assert.Equal("shop", url.DatabaseName);
        Assert.Equal("app user", url.Username);
        Assert.Equal("p@ss:word", url.Password);
        Assert.Equal("admin", url.AuthenticationSource);
        Assert.True(url.DirectConnection);
        Assert.Equal(5, url.MaxConnectionPoolSize);

        var atlas = server with { Host = "mongodb+srv://cluster0.example.net/?retryWrites=true", Port = null, Options = null, SslMode = "verify-full" };
        var built = ConnectionStringFactory.Build(atlas, "shop", "pw");
        Assert.StartsWith("mongodb+srv://", built, StringComparison.Ordinal);
        Assert.Contains("cluster0.example.net/shop", built, StringComparison.Ordinal);
        Assert.True(new MongoDB.Driver.MongoUrl(built).UseTls);

        Assert.Throws<Bengine.Introspection.IntrospectionException>(() => ConnectionStringFactory.Build(server with { Options = "maxPoolSize=many" }));
    }

    [Fact]
    public async Task A_user_without_a_password_is_refused_with_a_reason()
    {
        // The driver refuses such a credential before it connects (ArgumentException); it is the documented IntrospectionException.
        var server = new ServerProfile { Name = "m", Provider = DatabaseProvider.MongoDb, Host = "127.0.0.1", Port = 1, Username = "app" };
        var failure = await Assert.ThrowsAsync<Bengine.Introspection.IntrospectionException>(() => ObjectExplorer.TestAsync(server, null));
        Assert.Equal("The MongoDB user app needs a password: enter it, or clear the user name to connect without signing in.", failure.Message);
    }

    [Fact]
    public void Completes_collections_methods_fields_and_operators()
    {
        var catalog = new DatabaseCatalog(DatabaseProvider.MongoDb, "shop", null, [],
        [
            new CatalogRelation(null, "users", TableKind.Table,
            [
                new CatalogColumn("_id", "objectId", false, true),
                new CatalogColumn("name", "string", false, false),
                new CatalogColumn("address", "object", true, false),
                new CatalogColumn("address.city", "string", true, false),
            ]),
            new CatalogRelation(null, "order-lines", TableKind.Table, []),
            new CatalogRelation(null, "big_orders", TableKind.View, []),
        ], [], []);
        IReadOnlyList<string> At(string text) =>
            SqlCompletion.Complete(text, text.Length, catalog).Items.Select(i => i.InsertText).ToList();

        Assert.Contains("users", At("db."));
        Assert.Contains("getCollection(\"order-lines\")", At("db."));
        Assert.Contains("getCollectionNames", At("db.getC"));
        Assert.Equal("users", At("db.us")[0]);
        Assert.Contains("find", At("db.users."));
        Assert.Contains("sort", At("db.users.find({})."));
        Assert.Contains("toArray", At("db.users.aggregate([])."));
        Assert.Contains("name", At("db.users.find({"));
        Assert.Contains("\"address.city\"", At("db.users.find({ address.c"));
        Assert.Contains("$gt", At("db.users.find({ name: { $g"));
        Assert.Contains("$set", At("db.users.updateOne({ name: 'a' }, { $s"));
        Assert.Contains("$match", At("db.users.aggregate([{ $m"));
        Assert.Contains("$sum", At("db.users.aggregate([{ $group: { _id: null, n: { $s"));
        Assert.Empty(At("db.users.find({ name: 'db."));
        Assert.Contains("show collections", At(""));
    }

    [Fact]
    public void Parses_explain_output_into_a_plan()
    {
        var explain = BsonDocument.Parse("""
            {
              queryPlanner: { namespace: "shop.users", winningPlan: { queryPlan: { stage: "FETCH", inputStage: { stage: "IXSCAN", indexName: "name_1", keyPattern: { name: 1 }, direction: "forward" } } } },
              executionStats: { executionTimeMillis: 3, totalKeysExamined: 2, totalDocsExamined: 2,
                executionStages: { stage: "SORT", nReturned: 2, executionTimeMillisEstimate: 1, inputStage: { stage: "COLLSCAN", nReturned: 2, docsExamined: 1200, filter: { age: { $gt: 3 } } } } }
            }
            """);
        var planned = PlanReader.ParseMongo(explain, analyzed: false);
        var nodes = planned.Flatten().Select(n => n.Node).ToList();
        Assert.Equal(["FETCH", "IXSCAN"], nodes.Select(n => n.Operation));
        Assert.Equal("name_1", nodes[1].Object);

        var analyzed = PlanReader.ParseMongo(explain, analyzed: true);
        var stages = analyzed.Flatten().Select(n => n.Node).ToList();
        Assert.Equal(["SORT", "COLLSCAN"], stages.Select(n => n.Operation));
        Assert.Equal(3, analyzed.ExecutionTimeMs);
        Assert.Equal(2, stages[1].ActualRows);
        Assert.Contains(stages[1].Warnings, w => w.Contains("1,200 documents", StringComparison.Ordinal));
        Assert.Contains(stages[0].Warnings, w => w.Contains("In-memory sort", StringComparison.Ordinal));
        Assert.Equal("json", analyzed.RawFormat);
    }

    [Fact]
    public void Infers_fields_from_documents()
    {
        var documents = new List<BsonDocument>
        {
            BsonDocument.Parse("{_id: 1, name: 'a', address: {city: 'x', geo: {lat: 1}}, items: [{sku: 'a', qty: 1}]}"),
            BsonDocument.Parse("{_id: 2, name: 3, items: [{sku: 'b', price: 2.5}], tags: ['t']}"),
        };
        var top = MongoFields.Infer(documents);
        Assert.Equal(["_id", "name", "address", "items", "tags"], top.Select(f => f.Name));
        Assert.Equal("int | string", string.Join(" | ", top[1].Types.Order(StringComparer.Ordinal)));
        Assert.True(top[2].IsOptional && top[2].HasChildren);
        Assert.Equal("array<object>", top[3].TypeLabel);
        Assert.Equal("array<string>", top[4].TypeLabel);
        Assert.Equal(["items.sku", "items.qty", "items.price"], MongoFields.Infer(documents, "items").Select(f => f.Path));
        Assert.Contains(MongoFields.Flatten(documents), f => f.Path == "address.geo.lat");
    }

    [Fact]
    public void Builds_explorer_commands()
    {
        var users = new NodeRef { Kind = NodeKind.Table, Database = "shop", Relation = "users", Name = "users" };
        Assert.Equal("db.users.drop();", ObjectSql.Drop(DatabaseProvider.MongoDb, users));
        Assert.Equal("db.users.deleteMany({});", ObjectSql.Truncate(DatabaseProvider.MongoDb, users));
        Assert.Equal("db.users.renameCollection(\"people\");", ObjectSql.Rename(DatabaseProvider.MongoDb, users, "people"));
        var field = users with { Kind = NodeKind.Column, Name = "address.city" };
        Assert.Equal("db.users.updateMany({}, {$rename: {\"address.city\": \"address.town\"}});", ObjectSql.Rename(DatabaseProvider.MongoDb, field, "town"));
        Assert.Equal("db.users.updateMany({}, {$unset: {\"address.city\": \"\"}});", ObjectSql.Drop(DatabaseProvider.MongoDb, field));
        Assert.Equal("db.getCollection(\"order-lines\").dropIndex(\"sku_1\");",
            ObjectSql.Drop(DatabaseProvider.MongoDb, users with { Kind = NodeKind.Index, Relation = "order-lines", Name = "sku_1" }));
        Assert.False(ObjectSql.CanRename(DatabaseProvider.MongoDb, NodeKind.View));
        Assert.True(ObjectSql.CanDrop(DatabaseProvider.MongoDb, NodeKind.Database));
        foreach (var command in new[] { ObjectSql.Drop(DatabaseProvider.MongoDb, users), ObjectSql.Rename(DatabaseProvider.MongoDb, field, "town") })
        {
            Assert.NotNull(MongoShell.Parse(command));
        }

        var relation = new CatalogRelation(null, "users", TableKind.Table, [new CatalogColumn("_id", "objectId", false, true), new CatalogColumn("name", "string", false, false)]);
        foreach (var kind in new[] { "select", "insert", "update", "delete" })
        {
            Assert.NotNull(MongoShell.Parse(ObjectSql.Template(DatabaseProvider.MongoDb, relation, kind)));
        }

        var script = MongoObjectTree.CollectionScript(
            BsonDocument.Parse("{name: 'users', type: 'collection', options: {validator: {name: {$type: 'string'}}}}"),
            [BsonDocument.Parse("{v: 2, key: {_id: 1}, name: '_id_'}"), BsonDocument.Parse("{v: 2, key: {email: 1}, name: 'email_1', unique: true}")]);
        Assert.Equal(2, SqlScript.Split(script, DatabaseProvider.MongoDb).Count);
        Assert.Contains("db.users.createIndex({email: 1}, {name: \"email_1\", unique: true});", script, StringComparison.Ordinal);
    }
}
