# Bengine

A provider-independent **database schema AST** framework in C# — not an ORM. A
schema is modelled as a strictly-typed, immutable tree and rendered to SQL DDL
(PostgreSQL, SQL Server, MySQL, SQLite, Oracle) and language models (C#, Go, Rust,
TypeScript, Python, PHP) through a metadata-driven mapping and generation layer.

## Layout

```
bengine/
├── src/
│   ├── Bengine/              Framework library (the AST, metadata, mapping,
│   │                         validation, generators, visitors, serialization)
│   ├── Bengine.Introspection/ Live-database connectivity: connect, discover,
│   │                         introspect into the AST, and browse table data
│   ├── Bengine.Studio/       Bengine Data Studio, the desktop database IDE
│   ├── Bengine.Cli/          Console demo: schema.json → DDL + models
│   └── Bengine.Api/          ASP.NET Core API for the designer (generation, import)
├── designer/                 React + React Flow schema designer
├── examples/                 Sample schema.json documents
└── Bengine.slnx
```

The single contract between the pieces is **schema.json**: the designer produces
it, `Bengine.SchemaSerializer` deserializes it into the AST, and the generators
consume the AST.

The AST can also be built **from existing SQL**: `Bengine.Parsing` reverses the
generators, parsing `CREATE TABLE` / `CREATE INDEX` / `ALTER TABLE` DDL into the
AST and auto-detecting the source dialect (PostgreSQL, SQL Server, MySQL,
SQLite, Oracle — including the DDL that `DBMS_METADATA` and SQL Developer
write). The API exposes this at `POST /api/import/sql`, and the designer's
**Import SQL** button feeds pasted DDL straight onto the canvas.

## Database Explorer (live databases)

The AST can also be built **from a running database**. `Bengine.Introspection`
connects to a live PostgreSQL, SQL Server, MySQL, SQLite, or Oracle database — and
to MongoDB (below) — and offers three capabilities over one `DatabaseSession`, the
way a database tool does:

- **Discover** — list databases, schemas, and relations (`IDatabaseExplorer`).
- **Introspect** — read a relation, or a whole schema, into the same
  provider-independent `DatabaseSchema` AST the generators consume, so a live
  database round-trips straight back out as DDL, code, or a migration
  (`IDatabaseIntrospector`). This is the live-connection counterpart of
  `Bengine.Parsing`.
- **Browse** — read table data a page at a time with filtering and sorting
  (`ITableDataReader`). Identifiers are validated against the relation's real
  columns and quoted; every value is bound as a parameter, so queries cannot be
  injected.

```csharp
await using var session = await DatabaseSession.OpenAsync(
    DatabaseProvider.Sqlite, "Data Source=app.db");

foreach (var table in await session.Explorer.GetTablesAsync())
    Console.WriteLine(table.QualifiedName);

var page = await session.Data.QueryAsync(
    new TableRef("users"),
    new DataQuery
    {
        Page = 1,
        PageSize = 100,
        Filters = [FilterCondition.Contains("email", "@example.com")],
        Sort = [new SortSpec("created_at", SortDirection.Descending)],
        IncludeTotalCount = true,
    });
```

On top of browsing, the engine also **edits** data (`ITableDataWriter`:
insert/update/delete, keyed by primary key and fully parameterized), **runs
ad-hoc SQL** (`IQueryExecutor`, the query console), **renders DDL** from the live
catalog (`SchemaDdlRenderer`, reusing the generators), and **exports** rows as
CSV/JSON/SQL (`DataExporter`).

### Object explorer (pgAdmin-style)

`Bengine.Introspection.Tree` adds a lazily loaded object tree over **registered
servers**, following pgAdmin's hierarchy:

```
Servers (n)
└─ server
   ├─ Databases (n) ─ database
   │  ├─ Casts · Catalogs · Event Triggers · Extensions · Foreign Data Wrappers
   │  ├─ Languages · Publications · Subscriptions
   │  └─ Schemas (n) ─ schema
   │     ├─ Aggregates · Collations · Domains · Foreign Tables · Functions
   │     ├─ Materialized Views · Procedures · Sequences · Trigger Functions · Types · Views
   │     └─ Tables (n) ─ table ─ Columns · Constraints · Indexes · Triggers
   ├─ Login/Group Roles (n)
   └─ Tablespaces (n)
```

Every folder carries its child count, and every object has **Properties** and
**SQL** (PostgreSQL's own `pg_get_*def` output where it exists; tables are
rendered by Bengine's generator from the introspected AST). MySQL, SQL Server,
and SQLite use the subset of the hierarchy that applies to them; Oracle shows the
service connected to as the database, users as schemas (Oracle-maintained ones
under Catalogs), and adds packages and synonyms, with DDL from `DBMS_METADATA`.

Servers are saved in `~/.config/bengine/servers.json` (owner-only permissions;
`BENGINE_SERVERS_FILE` overrides the path), and can be **imported from a local pgAdmin 4 or from DataGrip** (and
the other JetBrains IDEs with the Database Tools plugin, such as Rider and
IntelliJ IDEA): the IDEs' global `options/dataSources.xml`, projects under
`~/DataGripProjects`, or any project's `.idea/dataSources.xml`. Neither tool
stores passwords in a readable form (pgAdmin encrypts them; DataGrip keeps them
in the OS keyring), so imported servers prompt once on first connect ("Save
password" stores it in the file; otherwise it is kept in memory for the
session). Oracle data sources are imported too (`@//host:port/service`,
`@host:port:SID`, and `(DESCRIPTION=…)` URLs; a TNS alias cannot be resolved),
and so are MongoDB ones (`mongodb://` and `mongodb+srv://`);
drivers Bengine does not support, such as DB2, are listed as skipped.

The engine's front end is **Bengine Data Studio** (`Bengine.Studio`), an
Avalonia desktop app over the engine in-process (below).

PostgreSQL results are read in text format, as pgAdmin does: every type
(including ones without a binary form, such as `aclitem` or extension types)
can be browsed, and numbers, timestamps, arrays, and JSON appear exactly as the
server prints them. Values typed into filters and edits are sent untyped, so
PostgreSQL coerces them to the column's type (`uuid`, `timestamp`, `bigint`, …).

**Oracle** (12c and later, through the managed ODP.NET driver — no Oracle client
install) connects to a service (`host:port/service`, TCPS when SSL is set). Each
session reads and writes dates and numbers in ISO form, so typed values convert
regardless of the server's NLS settings, and `NUMBER` values come back exactly
(as text beyond `decimal`'s range). Scripts split the SQL*Plus way: `;` ends a SQL
statement, a PL/SQL block or unit runs to its `END;` or to a `/` on its own line,
and `q'[...]'` strings are understood. Generated DDL writes plain names bare, so
Oracle folds `users` to `USERS` and checks and views resolve; against a live
database, names stay exactly as stored. Explain Analyze and Server Activity read
`V$` views, which need `SELECT_CATALOG_ROLE`.

**MongoDB** (through the official MongoDB .NET driver) is browsed like a
database in DataGrip, in Studio and the engine (it is not a target of the SQL
generators). Register it with a host and port — or paste a whole
`mongodb+srv://` URL into the host field (Atlas) — with the authentication
database (`admin` by default) and URL options (`replicaSet=rs0`, …) on the
Advanced tab; SSH tunnels and TLS work as for the other servers. The tree shows
databases, collections (capped and time-series marked), views, each
collection's fields inferred from a sample of 100 documents (embedded documents
and arrays of documents expand), indexes, and users; a collection's "DDL" is the
`db.createCollection` / `createIndex` script that recreates it. The data editor
shows one column per top-level field, takes a filter and a sort as documents
(`{age: {$gt: 30}}`, `{name: 1}`), and edits documents by `_id`: changed fields
are `$set`, "Unset Field" `$unset`s one, typed text keeps the field's BSON type
(numbers, dates, `ObjectId`s, embedded documents in shell syntax), and a new
document without an `_id` gets one from the server. On a replica set or sharded
cluster a change set is one transaction; a standalone server has none, so
changes apply in order and stop at the first failure. The console speaks the
`mongosh` shell for data — `show dbs`, `db.coll.find({…}).sort({…}).limit(n)`,
`aggregate([…])`, counts and `distinct`, the write and index methods,
`db.runCommand({…})`, and bare command documents — with completion of
collections, methods, fields, and operators; it is not a JavaScript runtime
(no variables or functions). Explain shows the winning plan (analyzed: the
execution stages with documents examined), Server Activity lists `currentOp`
and kills operations, and a read-only registration refuses commands that can
write. Schema and data compare, dumps, and the diagram are relational only.

> Studio acts with the credentials you give it: the Data tabs and Query Tool
> **modify live data** when you edit rows or run DML/DDL; connect only to
> databases you are authorized to use.

### Bengine Data Studio (desktop)

Everything in the explorer above, plus the tools of a database IDE, for
PostgreSQL, MySQL/MariaDB, SQL Server, SQLite, and Oracle (MongoDB as described above):

- **Database Explorer** — a DataGrip-style tree: data sources at the root,
  databases, schemas, and columns without intermediate folders, object kinds
  as folders with counts (`tables 12`, `routines`, `keys`, `foreign keys`, …),
  and server- and database-wide objects tucked away. Double-click (or F4)
  opens a table's data, a column's table, or another object's DDL; Ctrl+B
  goes to DDL, Ctrl+Q shows quick documentation, Ctrl+Alt+Shift+C copies the
  qualified name, and typing in the tree jumps to the matching row.
- **SQL editor** — completion of tables, columns, and foreign-key joins; the
  statement under the caret (Ctrl+Enter) or the script (Ctrl+Shift+Enter);
  cancel; parameters (`:name`, `?`); reformat; live templates; on-the-fly
  inspections (unknown names, `UPDATE`/`DELETE` without `WHERE`, `= NULL`, …);
  query history; manual transactions with isolation levels.
- **Data editor** — edits, inserts, and deletes staged until Submit (with a SQL
  preview), `WHERE`/`ORDER BY` filters, foreign-key navigation, copy as
  CSV/TSV/Markdown/JSON/SQL, XLSX export, and CSV import into new or existing tables.
- **Schema tools** — Modify/New Table dialogs that generate `ALTER` scripts
  (SQLite table rebuilds included), schema compare with a migration script,
  ER diagrams, Find Usages (Alt+F7), and renames that also update open SQL.
- **Plans and activity** — Explain / Explain Analyze as a plan tree with
  warnings, and server sessions and locks with cancel and terminate.
- **Files** — a Files panel for the project folder and scratch files; SQL files
  open in Query Tools bound to them (Ctrl+S), remember their data source, and
  keep a local history with diffs.
- **Dump and restore** — pg_dump/pg_restore/psql and mysqldump/mysql (password
  passed in the environment, SSH tunnels honored), SQL Server `BACKUP`/`RESTORE`,
  SQLite file copies, and a portable Bengine SQL dump that any provider can replay
  (Oracle dumps through it: `INSERT ALL` batches, identities restarted after the load).
- **Data compare** — two tables (any servers, any providers) compared by key,
  with the `INSERT`/`UPDATE`/`DELETE` that sync the target.
- **Connections** — SSH tunnels (host keys trusted on first use), SSL modes and
  certificates, passwords in the OS keyring, per-server colors, and read-only servers.
- **Keymap** — every shortcut can be rebound, and Find Action (Ctrl+Shift+A)
  runs any action by name. Settings live in `~/.config/bengine/studio.json`.
- **Icons** — files and the object-kind folders of the tree use icons from the
  [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme)
  (MIT; the SVGs and their license are in `src/Bengine.Studio/Assets/Icons`).
- **Workspace restore** — closing and reopening the Studio brings back the open
  tabs (consoles with their text, unsaved edits of SQL files included; data
  editors with their filter and sort; DDL, activity, and diagrams), the expanded
  and selected tree rows, and the window's size, place, and side panel. Restored
  tabs connect when first shown; servers that were open reconnect at once only
  when no password has to be typed. The state is saved on exit and every 20
  seconds to `~/.config/bengine/workspace.json` (owner-only; no passwords or
  results).

## Run

Build everything:

```bash
dotnet build Bengine.slnx
```

Generate from a schema.json — or import a raw `.sql` DDL script (the dialect is
auto-detected) — with the CLI:

```bash
dotnet run --project src/Bengine.Cli -- examples/shop.schema.json
dotnet run --project src/Bengine.Cli -- examples/postgres.sql
```

Start the generation API (used by the designer):

```bash
dotnet run --project src/Bengine.Api        # http://localhost:5099
```

Run the designer (proxies `/api` to the backend):

```bash
cd designer
npm install
npm run dev                                  # http://localhost:5173
```

Run Bengine Data Studio (Avalonia):

```bash
dotnet run --project src/Bengine.Studio
```

### Releasing Bengine Data Studio

Pushing a `studio-vX.Y.Z` tag runs `.github/workflows/release-studio.yml`: it runs
the tests, publishes Studio self-contained (no .NET runtime needed) for Linux,
Windows, and macOS, and attaches the packages to a GitHub Release:

| Platform | Package |
| --- | --- |
| Linux | `bengine-studio_X_amd64.deb`, `BengineDataStudio-X-x86_64.AppImage` |
| Windows | `BengineDataStudio-Setup-X-x64.exe` (per-user, no admin prompt) |
| macOS | `BengineDataStudio-X-arm64.dmg`, `BengineDataStudio-X-x64.dmg` |

```bash
git tag studio-v1.0.0 && git push origin studio-v1.0.0
```

Running the workflow manually builds the packages as artifacts without
releasing. The packages are unsigned, so Windows SmartScreen and macOS Gatekeeper
warn on first launch. Icons and packaging scripts live in `deploy/studio`
(`make-icons.py` regenerates the icons; `package-linux.sh` also runs locally).

Live-database tests run when `BENGINE_TEST_PG`, `BENGINE_TEST_MYSQL`,
`BENGINE_TEST_MARIADB`, or `BENGINE_TEST_MSSQL` is set to
`host;port;database;user;password` (each test works in a scratch database it
creates and drops); `BENGINE_TEST_ORACLE` (`host;port;service;admin;password`)
runs the Oracle tests in scratch users it creates and drops; `BENGINE_TEST_MONGO`
(`host;port;authDatabase;user;password`) runs the MongoDB tests in scratch
databases it drops; `BENGINE_TEST_SSH` adds the SSH tunnel test and
`BENGINE_TEST_KEYRING=1` the OS keyring test. The desktop UI tests render
screenshots to the directory named by `BENGINE_SHOTS`.

## Design rules

See [src/Bengine/SPEC.md](src/Bengine/SPEC.md) for the full framework
requirements. In short: strict typing (enums over strings, no `dynamic`),
immutability (`record` value objects, `FrozenDictionary` maps), metadata as the
single source of truth, and a clean ownership model — a column knows nothing of
indexes or keys; the table owns its constraints; the database owns its objects.

## License

Bengine is released under the [MIT License](LICENSE). The Studio's file and
folder icons come from the Material Icon Theme, also MIT
(`src/Bengine.Studio/Assets/Icons/LICENSE.txt`).
