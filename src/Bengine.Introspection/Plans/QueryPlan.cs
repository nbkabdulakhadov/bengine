namespace Bengine.Introspection.Plans;

/// <summary>One operation of an execution plan.</summary>
public sealed class PlanNode
{
    /// <summary>Gets or sets the operation (<c>Seq Scan</c>, <c>Hash Join</c>, <c>Index Seek</c>).</summary>
    public required string Operation { get; set; }

    /// <summary>Gets or sets the object operated on (table, index), or <c>null</c>.</summary>
    public string? Object { get; set; }

    /// <summary>Gets the other properties worth showing (conditions, keys, buffers).</summary>
    public List<KeyValuePair<string, string>> Details { get; } = [];

    /// <summary>Gets or sets the optimizer's startup cost.</summary>
    public double? StartupCost { get; set; }

    /// <summary>Gets or sets the optimizer's total cost (inclusive of children).</summary>
    public double? TotalCost { get; set; }

    /// <summary>Gets or sets the estimated rows (per loop).</summary>
    public double? EstimatedRows { get; set; }

    /// <summary>Gets or sets the actual rows (per loop), when the statement was executed.</summary>
    public double? ActualRows { get; set; }

    /// <summary>Gets or sets the actual time in milliseconds (per loop, inclusive of children).</summary>
    public double? ActualTimeMs { get; set; }

    /// <summary>Gets or sets how many times the operation ran.</summary>
    public double? Loops { get; set; }

    /// <summary>Gets the child operations.</summary>
    public List<PlanNode> Children { get; } = [];

    /// <summary>Gets the warnings found for the operation (full scans, misestimates).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Gets the total time spent in this operation alone (all loops), when measured.</summary>
    public double? SelfTimeMs => ActualTimeMs is { } time
        ? Math.Max(0, (time * (Loops ?? 1)) - Children.Sum(c => (c.ActualTimeMs ?? 0) * (c.Loops ?? 1)))
        : null;

    /// <summary>Enumerates the node and its descendants depth-first, with depth.</summary>
    /// <param name="depth">The depth of this node.</param>
    /// <returns>The nodes.</returns>
    public IEnumerable<(PlanNode Node, int Depth)> Flatten(int depth = 0)
    {
        yield return (this, depth);
        foreach (var child in Children)
        {
            foreach (var item in child.Flatten(depth + 1))
            {
                yield return item;
            }
        }
    }
}

/// <summary>
/// An execution plan in a provider-independent shape: a tree of operations with
/// costs, row estimates, and — when the statement was executed (explain
/// analyze) — actual rows and times, plus the raw plan text the server
/// returned.
/// </summary>
public sealed class QueryPlan
{
    /// <summary>Gets the top-level operations (one per statement part).</summary>
    public List<PlanNode> Roots { get; } = [];

    /// <summary>Gets or sets whether the statement was executed (actual figures present).</summary>
    public bool Analyzed { get; set; }

    /// <summary>Gets or sets the planning time, when reported.</summary>
    public double? PlanningTimeMs { get; set; }

    /// <summary>Gets or sets the execution time, when reported.</summary>
    public double? ExecutionTimeMs { get; set; }

    /// <summary>Gets or sets the plan as the server printed it (JSON, XML, or text).</summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>Gets or sets the format of <see cref="Raw"/>: json, xml, or text.</summary>
    public string RawFormat { get; set; } = "text";

    /// <summary>Gets every node depth-first.</summary>
    /// <returns>The nodes with their depth.</returns>
    public IEnumerable<(PlanNode Node, int Depth)> Flatten() => Roots.SelectMany(r => r.Flatten());

    /// <summary>Adds warnings: full scans of many rows, and estimates off by an order of magnitude.</summary>
    public void Annotate()
    {
        foreach (var (node, _) in Flatten())
        {
            var rows = node.ActualRows ?? node.EstimatedRows ?? 0;
            var scan = node.Operation.Contains("Seq Scan", StringComparison.OrdinalIgnoreCase)
                || node.Operation.StartsWith("Table scan", StringComparison.OrdinalIgnoreCase)
                || node.Operation.Equals("Table Scan", StringComparison.OrdinalIgnoreCase)
                || node.Operation.Equals("Clustered Index Scan", StringComparison.OrdinalIgnoreCase)
                || node.Operation.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase)
                || node.Operation.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                || node.Operation.Equals("TABLE ACCESS FULL", StringComparison.OrdinalIgnoreCase);
            // A scan that reads many rows and keeps few is the usual missing index, so count what it read:
            // the rows it returned plus those its filter threw away (PostgreSQL's actual plans report them).
            var loops = node.Loops ?? 1;
            var removed = RowsRemovedByFilter(node);
            if (scan && (rows + removed) * loops >= 1000)
            {
                node.Warnings.Add(removed > 0
                    ? $"Full scan of {(rows + removed) * loops:N0} rows that keeps {rows * loops:N0} — an index on the filtered columns may help."
                    : $"Full scan of {rows * loops:N0} rows — an index on the filtered columns may help.");
            }

            if (node is { ActualRows: { } actual, EstimatedRows: { } estimate } && (actual > 0 || estimate > 0))
            {
                var ratio = Math.Max(actual, 1) / Math.Max(estimate, 1);
                if (ratio >= 10 || ratio <= 0.1)
                {
                    node.Warnings.Add($"Row estimate is off by {Math.Max(ratio, 1 / ratio):N0}× ({estimate:N0} estimated, {actual:N0} actual) — statistics may be stale.");
                }
            }
        }
    }

    /// <summary>Gets the rows a node's filter discarded per loop, or 0 when the plan does not say.</summary>
    private static double RowsRemovedByFilter(PlanNode node) =>
        node.Details.FirstOrDefault(d => d.Key == "Rows Removed by Filter").Value is { } text
            && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var removed)
            ? removed
            : 0;
}
