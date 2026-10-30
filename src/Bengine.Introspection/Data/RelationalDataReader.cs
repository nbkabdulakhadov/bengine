namespace Bengine.Introspection.Data;

using System.Data.Common;
using System.Globalization;
using System.Text;
using Bengine.Enums;
using Bengine.Introspection.Abstractions;
using Bengine.Introspection.Model;

/// <summary>
/// A provider-independent <see cref="ITableDataReader"/> over an open
/// <see cref="DbConnection"/>. It learns the relation's real columns up front,
/// validates every filter and sort column against them, quotes identifiers with
/// the provider's dialect, and binds all values as parameters. Paging is
/// rendered with the provider's native syntax: <c>LIMIT/OFFSET</c> for
/// PostgreSQL, MySQL, and SQLite, and <c>OFFSET/FETCH</c> for SQL Server and Oracle.
/// </summary>
public sealed class RelationalDataReader : RelationalComponentBase, ITableDataReader
{
    /// <summary>Creates a reader bound to an open connection for the given provider.</summary>
    /// <param name="connection">An open connection; the reader does not own or dispose it.</param>
    /// <param name="provider">The provider whose SQL dialect to emit.</param>
    public RelationalDataReader(DbConnection connection, DatabaseProvider provider)
        : base(connection, provider)
    {
    }

    /// <inheritdoc />
    /// <exception cref="IntrospectionException">The server rejected the query: a filter or sort the user typed, a relation
    /// gone, a connection lost.</exception>
    public async Task<DataPage> QueryAsync(
        TableRef table,
        DataQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadPageAsync(table, query, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new IntrospectionException(ex.Message, ex);
        }
    }

    private async Task<DataPage> ReadPageAsync(TableRef table, DataQuery query, CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(table, cancellationToken).ConfigureAwait(false);
        var lookup = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);

        var qualified = QualifiedName(table);
        var parameters = new List<(string Name, object Value)>();
        var where = BuildWhere(query.Filters, lookup, parameters);
        if (!string.IsNullOrWhiteSpace(query.WhereSql))
        {
            where = (where.Length == 0 ? " WHERE " : where + " AND ") + $"({query.WhereSql.Trim().TrimEnd(';')})";
        }

        var orderBy = string.IsNullOrWhiteSpace(query.OrderBySql)
            ? BuildOrderBy(query.Sort, lookup)
            : " ORDER BY " + query.OrderBySql.Trim().TrimEnd(';');

        long? total = null;
        if (query.IncludeTotalCount)
        {
            total = await CountAsync(qualified, where, parameters, cancellationToken).ConfigureAwait(false);
        }

        var fetch = query.PageSize + 1;
        var sql = BuildPageSql(qualified, where, orderBy, query.Offset, fetch);

        await using var command = CreateCommand(sql, parameters);
        PrepareForDisplay(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Bengine.Introspection.Connection.OracleSupport.Prepare(reader);

        var resultColumns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            resultColumns[i] = reader.GetName(i);
        }

        var resultTypes = ColumnTypeNames(reader);

        var rows = new List<IReadOnlyList<object?>>(query.PageSize);
        var hasMore = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == query.PageSize)
            {
                hasMore = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = Bengine.Introspection.Connection.OracleSupport.GetValue(reader, i);
                row[i] = value is DBNull ? null : FromDisplayText(value, resultTypes[i]);
            }

            rows.Add(row);
        }

        return new DataPage
        {
            Sql = DisplaySql(qualified, where, orderBy, parameters),
            Columns = resultColumns,
            ColumnTypes = resultTypes,
            Rows = rows,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = total,
            HasMore = hasMore,
        };
    }

    private string BuildWhere(
        IReadOnlyList<FilterCondition> filters,
        IReadOnlySet<string> columns,
        List<(string Name, object Value)> parameters)
    {
        if (filters.Count == 0)
        {
            return string.Empty;
        }

        var clauses = new List<string>(filters.Count);
        foreach (var filter in filters)
        {
            EnsureKnownColumn(filter.Column, columns);
            var column = Dialect.QuoteIdentifier(filter.Column);
            clauses.Add(RenderFilter(column, filter, parameters));
        }

        return " WHERE " + string.Join(" AND ", clauses);
    }

    // MySQL treats backslash as an escape inside string literals, so its LIKE
    // escape character must itself be escaped.
    private string EscapeClause => Provider == DatabaseProvider.MySql ? " ESCAPE '\\\\'" : " ESCAPE '\\'";

    // Pattern filters compare case-insensitively against the column rendered as
    // text, so "contains" works on any column type.
    private string Like(string column, List<(string Name, object Value)> parameters, string pattern) =>
        $"LOWER({AsText(column)}) LIKE LOWER({AddParameter(parameters, pattern)}){EscapeClause}";

    private string RenderFilter(
        string column,
        FilterCondition filter,
        List<(string Name, object Value)> parameters)
    {
        switch (filter.Operator)
        {
            case FilterOperator.IsNull:
                return $"{column} IS NULL";
            case FilterOperator.IsNotNull:
                return $"{column} IS NOT NULL";
            case FilterOperator.In:
            {
                if (filter.Values.Count == 0)
                {
                    return "1 = 0";
                }

                var names = new List<string>(filter.Values.Count);
                foreach (var value in filter.Values)
                {
                    names.Add(AddParameter(parameters, value));
                }

                return $"{column} IN ({string.Join(", ", names)})";
            }

            case FilterOperator.Contains:
                return Like(column, parameters, $"%{EscapeLike(AsString(filter.Value))}%");
            case FilterOperator.StartsWith:
                return Like(column, parameters, $"{EscapeLike(AsString(filter.Value))}%");
            case FilterOperator.EndsWith:
                return Like(column, parameters, $"%{EscapeLike(AsString(filter.Value))}");
            default:
            {
                var op = filter.Operator switch
                {
                    FilterOperator.Equals => "=",
                    FilterOperator.NotEquals => "<>",
                    FilterOperator.LessThan => "<",
                    FilterOperator.LessThanOrEqual => "<=",
                    FilterOperator.GreaterThan => ">",
                    FilterOperator.GreaterThanOrEqual => ">=",
                    _ => "=",
                };

                if (filter.Value is null)
                {
                    return filter.Operator == FilterOperator.NotEquals
                        ? $"{column} IS NOT NULL"
                        : $"{column} IS NULL";
                }

                return $"{column} {op} {AddParameter(parameters, filter.Value)}";
            }
        }
    }

    private string BuildOrderBy(IReadOnlyList<SortSpec> sort, IReadOnlySet<string> columns)
    {
        if (sort.Count == 0)
        {
            return string.Empty;
        }

        var terms = new List<string>(sort.Count);
        foreach (var term in sort)
        {
            EnsureKnownColumn(term.Column, columns);
            var direction = term.Direction == SortDirection.Descending ? " DESC" : " ASC";
            terms.Add(Dialect.QuoteIdentifier(term.Column) + direction);
        }

        return " ORDER BY " + string.Join(", ", terms);
    }

    private string BuildPageSql(string qualified, string where, string orderBy, int offset, int fetch)
    {
        var sql = new StringBuilder();
        sql.Append("SELECT * FROM ").Append(qualified).Append(where);

        if (Provider == DatabaseProvider.Oracle)
        {
            // Oracle 12c and later; unlike SQL Server, no ORDER BY is required.
            sql.Append(orderBy);
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET {offset} ROWS FETCH NEXT {fetch} ROWS ONLY");
            return sql.ToString();
        }

        if (Provider == DatabaseProvider.SqlServer)
        {
            sql.Append(orderBy.Length == 0 ? " ORDER BY (SELECT NULL)" : orderBy);
            sql.Append(CultureInfo.InvariantCulture, $" OFFSET {offset} ROWS FETCH NEXT {fetch} ROWS ONLY");
            return sql.ToString();
        }

        sql.Append(orderBy);
        sql.Append(CultureInfo.InvariantCulture, $" LIMIT {fetch} OFFSET {offset}");
        return sql.ToString();
    }

    private async Task<long> CountAsync(
        string qualified,
        string where,
        List<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(*) FROM {qualified}{where}";
        await using var command = CreateCommand(sql, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    // The page's query without paging, parameters inlined, for "open in console".
    private string DisplaySql(string qualified, string where, string orderBy, List<(string Name, object Value)> parameters)
    {
        var sql = $"SELECT * FROM {qualified}{where}{orderBy}";
        for (var i = parameters.Count - 1; i >= 0; i--)
        {
            var (name, value) = parameters[i];
            sql = sql.Replace(name, Export.SqlLiterals.Format(value is DBNull ? null : value, Provider), StringComparison.Ordinal);
        }

        return sql;
    }

    private static string AsString(object? value) => value?.ToString() ?? string.Empty;

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
