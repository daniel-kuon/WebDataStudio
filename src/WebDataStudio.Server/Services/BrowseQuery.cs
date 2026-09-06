using System.Globalization;
using WebDataStudio.Server.Drivers.Abstractions;

namespace WebDataStudio.Server.Services;

/// One filter on the browsed table. `Column` addresses a base column by its name, or a joined
/// column as "label.column" — the same names the result carries, so what the user filters is what
/// they see.
public sealed record BrowseFilter(string Column, string Op, string? Value);

/// One ORDER BY entry. Several of them sort by the first, then the second, and so on.
public sealed record BrowseSort(string Column, bool Desc);

/// One aggregate of a grouped view: count, sum, avg, min or max. `Column` is null only for count,
/// which counts rows rather than values.
public sealed record BrowseAggregate(string Function, string? Column);

/// Everything a browse may ask for beyond paging: filters, ordering, joins along the table's own
/// foreign keys, and grouping with aggregates.
public sealed record BrowseQueryInput(
    IReadOnlyList<BrowseFilter> Filters,
    IReadOnlyList<BrowseSort> Sort,
    IReadOnlyList<string> Joins,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<BrowseAggregate> Aggregates);

/// A borrowed column, resolved: "customer_id.name" is `Column` of the table `Key` points at, shown
/// next to the id instead of being reached by following it.
public sealed record ResolvedLookup(ForeignKeyInfo Key, string Name, ColumnInfo Column, SchemaNodeRef TargetRef);

/// Builds the SELECT behind the data tab's query bar. Everything here is validated against the
/// real schema before it goes anywhere near SQL: identifiers are resolved to columns that exist
/// and quoted, values travel as parameters, and a join can only follow a foreign key the table
/// actually declares. A request that names anything else fails as a FormatException, which the
/// endpoint answers as 400 — loudly, rather than as an engine error later.
public static class BrowseQuery
{
    /// A join the caller picked, resolved: the foreign key, the referenced table described, and the
    /// label its columns carry in the result ("customers.name").
    public sealed record ResolvedJoin(ForeignKeyInfo Key, string Label, ObjectDetail Detail);

    private static readonly string[] AggregateFunctions = ["count", "sum", "avg", "min", "max"];

    private static readonly string[] Operators =
        ["contains", "startswith", "endswith", "eq", "neq", "gt", "gte", "lt", "lte", "null", "notnull",
         // The column box's small filter language (see FilterExpression); the value is the whole
         // expression and the operator lives inside it.
         "expr"];

    /// The label a join's columns carry: the referenced table's name, unless two selected keys
    /// reference the same table — then the key's own name, which is unique per table.
    public static string LabelFor(ForeignKeyInfo key, IReadOnlyList<ForeignKeyInfo> selected) =>
        selected.Count(other => other.ReferencedTable.Equals(key.ReferencedTable, StringComparison.OrdinalIgnoreCase)) > 1
            ? key.Name
            : key.ReferencedTable;

    /// The schema node a foreign key points at; an unqualified referenced table means "the same
    /// schema", the way every engine reads it.
    public static SchemaNodeRef ReferencedRef(ForeignKeyInfo key, SchemaNodeRef from) =>
        new(SchemaNodeKind.Table,
            key.ReferencedSchema is { Length: > 0 } ? [key.ReferencedSchema, key.ReferencedTable]
            : from.Path.Count > 1 ? [from.Path[0], key.ReferencedTable]
            : [key.ReferencedTable]);

    /// The alias an aggregate answers under — "count(*)", "sum(amount)" — which is also how a sort
    /// addresses it.
    public static string AggregateAlias(BrowseAggregate aggregate) =>
        $"{aggregate.Function.ToLowerInvariant()}({aggregate.Column ?? "*"})";

    public static (string Sql, Dictionary<string, string?> Parameters) Build(
        string baseTable, ObjectDetail detail, IReadOnlyList<ResolvedJoin> joins,
        IReadOnlyList<ResolvedLookup> lookups, BrowseQueryInput input, SqlDialect dialect,
        string charType, string? rowAddress = null)
    {
        // Every addressable column, resolved to its aliased SQL expression and its declared type
        // (the filter language compares a number column as a number). Base columns keep their
        // plain name; joined and borrowed ones are "label.column" — result aliases and addresses
        // match.
        var expressions = new Dictionary<string, (string Sql, string DataType)>(StringComparer.OrdinalIgnoreCase);
        var select = new List<string>();

        // A table with no key is addressed by where its rows physically are, and that only works
        // if the address comes back with them — the same wds_row_address the plain browse selects.
        if (rowAddress is { Length: > 0 })
            select.Add($"t.{rowAddress} AS {dialect.QuoteIdentifier(Editing.RowIdentity.AddressColumn)}");

        foreach (var column in detail.Columns.OrderBy(c => c.Position))
        {
            var expression = $"t.{dialect.QuoteIdentifier(column.Name)}";
            expressions[column.Name] = (expression, column.DataType);
            select.Add($"{expression} AS {dialect.QuoteIdentifier(column.Name)}");
        }

        var from = $"{baseTable} t";

        for (var i = 0; i < joins.Count; i++)
        {
            var join = joins[i];
            var alias = $"j{i}";
            var table = Qualify(ReferencedRef(join.Key, detail.Ref), dialect);

            var conditions = new List<string>();
            for (var c = 0; c < join.Key.Columns.Count && c < join.Key.ReferencedColumns.Count; c++)
                conditions.Add($"t.{dialect.QuoteIdentifier(join.Key.Columns[c])} = " +
                               $"{alias}.{dialect.QuoteIdentifier(join.Key.ReferencedColumns[c])}");

            if (conditions.Count == 0)
                throw new FormatException($"foreign key '{join.Key.Name}' has no columns to join on");

            // LEFT, not INNER: a row whose foreign key is null is still a row of this table, and
            // joining must never make rows disappear.
            from += $" LEFT JOIN {table} {alias} ON {string.Join(" AND ", conditions)}";

            foreach (var column in join.Detail.Columns.OrderBy(c => c.Position))
            {
                var name = $"{join.Label}.{column.Name}";
                var expression = $"{alias}.{dialect.QuoteIdentifier(column.Name)}";
                expressions[name] = (expression, column.DataType);
                select.Add($"{expression} AS {dialect.QuoteIdentifier(name)}");
            }
        }

        // Borrowed columns join like a picked key does, but bring exactly one column each.
        for (var i = 0; i < lookups.Count; i++)
        {
            var lookup = lookups[i];
            var alias = $"l{i}";

            from += $" LEFT JOIN {Qualify(lookup.TargetRef, dialect)} {alias}" +
                    $" ON {alias}.{dialect.QuoteIdentifier(lookup.Key.ReferencedColumns[0])}" +
                    $" = t.{dialect.QuoteIdentifier(lookup.Key.Columns[0])}";

            var expression = $"{alias}.{dialect.QuoteIdentifier(lookup.Column.Name)}";
            expressions[lookup.Name] = (expression, lookup.Column.DataType);
            select.Add($"{expression} AS {dialect.QuoteIdentifier(lookup.Name)}");
        }

        string Resolve(string column) =>
            expressions.TryGetValue(column, out var entry)
                ? entry.Sql
                : throw new FormatException($"no column '{column}' on this table or its joins");

        string TypeOf(string column) =>
            expressions.TryGetValue(column, out var entry) ? entry.DataType : "";

        var parameters = new Dictionary<string, string?>();
        var conditionsSql = input.Filters
            .Select(filter => Condition(filter, Resolve(filter.Column), TypeOf(filter.Column),
                dialect, charType, parameters))
            .Where(sql => sql.Length > 0)
            .ToList();
        var where = conditionsSql.Count > 0 ? $" WHERE {string.Join(" AND ", conditionsSql)}" : "";

        var grouped = input.GroupBy.Count > 0 || input.Aggregates.Count > 0;
        var groupBy = "";

        if (grouped)
        {
            // A grouped view answers with the grouped columns and the aggregates, nothing else —
            // any other column has no single value per group to show.
            select = [.. input.GroupBy.Select(column =>
                $"{Resolve(column)} AS {dialect.QuoteIdentifier(column)}")];

            // Grouping without an aggregate still needs something to say about each group.
            var aggregates = input.Aggregates.Count > 0
                ? input.Aggregates
                : [new BrowseAggregate("count", null)];

            foreach (var aggregate in aggregates)
            {
                var function = aggregate.Function.ToLowerInvariant();
                if (!AggregateFunctions.Contains(function))
                    throw new FormatException(
                        $"'{aggregate.Function}' is not an aggregate; use one of {string.Join(", ", AggregateFunctions)}");

                if (aggregate.Column is null && function != "count")
                    throw new FormatException($"{function} needs a column");

                var argument = aggregate.Column is null ? "*" : Resolve(aggregate.Column);
                select.Add($"{function.ToUpperInvariant()}({argument}) " +
                           $"AS {dialect.QuoteIdentifier(AggregateAlias(aggregate))}");
            }

            if (select.Count == 0)
                throw new FormatException("a grouped view needs at least one group column or aggregate");

            if (input.GroupBy.Count > 0)
                groupBy = $" GROUP BY {string.Join(", ", input.GroupBy.Select(Resolve))}";
        }

        var order = OrderBy(input, grouped, Resolve, dialect);

        return ($"SELECT {string.Join(", ", select)} FROM {from}{where}{groupBy}{order}", parameters);
    }

    /// The ORDER BY, validated against what the SELECT actually answers: in a grouped view only the
    /// grouped columns and the aggregate aliases exist to sort by.
    private static string OrderBy(BrowseQueryInput input, bool grouped,
        Func<string, string> resolve, SqlDialect dialect)
    {
        if (input.Sort.Count == 0) return "";

        var aliases = input.Aggregates.Select(AggregateAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grouped && input.Aggregates.Count == 0) aliases.Add("count(*)");

        var entries = input.Sort.Select(sort =>
        {
            string expression;
            if (grouped)
            {
                if (aliases.Contains(sort.Column)) expression = dialect.QuoteIdentifier(sort.Column);
                else if (input.GroupBy.Contains(sort.Column, StringComparer.OrdinalIgnoreCase))
                    expression = resolve(sort.Column);
                else
                    throw new FormatException(
                        $"a grouped view can only be sorted by its group columns and aggregates, not '{sort.Column}'");
            }
            else
            {
                expression = resolve(sort.Column);
            }

            return expression + (sort.Desc ? " DESC" : "");
        });

        return $" ORDER BY {string.Join(", ", entries)}";
    }

    /// One filter as SQL. Values travel as parameters; the only thing ever inlined is a number that
    /// survived a strict invariant parse, because a range comparison on a CAST-to-text column would
    /// order "9" after "10". Text comparisons go through the same CAST the single-column filter
    /// always used, so they behave identically on every engine.
    private static string Condition(BrowseFilter filter, string expression, string dataType,
        SqlDialect dialect, string charType, Dictionary<string, string?> parameters)
    {
        var op = filter.Op.ToLowerInvariant();
        if (!Operators.Contains(op))
            throw new FormatException($"'{filter.Op}' is not a filter; use one of {string.Join(", ", Operators)}");

        if (op is "null" or "notnull")
            return $"{expression} IS{(op == "notnull" ? " NOT" : "")} NULL";

        var value = filter.Value
            ?? throw new FormatException($"the {op} filter on '{filter.Column}' needs a value");

        // The column box's language, routed through the same builder the plain browse uses, so
        // `=a,=b` from the distinct list and `>10 <20` behave identically on both paths.
        if (op == "expr")
        {
            var condition = FilterExpression.Build(dialect, expression,
                FilterExpression.KindOf(dataType), value, $"f{parameters.Count}x");

            if (condition.IsEmpty) return "";

            foreach (var (key, parameterText) in FilterExpression.AsText(condition.Parameters))
                parameters[key] = parameterText;

            return $"({condition.Sql})";
        }

        var text = $"CAST({expression} AS {charType})";

        string Parameter(string parameterValue)
        {
            var name = $"f{parameters.Count}";
            parameters[name] = parameterValue;
            return dialect.ParameterPrefix + name;
        }

        var numeric = decimal.TryParse(value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number);
        var literal = numeric ? number.ToString(CultureInfo.InvariantCulture) : null;

        return op switch
        {
            "contains" => $"{text} LIKE {Parameter($"%{value}%")}",
            "startswith" => $"{text} LIKE {Parameter($"{value}%")}",
            "endswith" => $"{text} LIKE {Parameter($"%{value}")}",
            "eq" => literal is not null ? $"{expression} = {literal}" : $"{text} = {Parameter(value)}",
            "neq" => literal is not null ? $"{expression} <> {literal}" : $"{text} <> {Parameter(value)}",
            "gt" => literal is not null ? $"{expression} > {literal}" : $"{text} > {Parameter(value)}",
            "gte" => literal is not null ? $"{expression} >= {literal}" : $"{text} >= {Parameter(value)}",
            "lt" => literal is not null ? $"{expression} < {literal}" : $"{text} < {Parameter(value)}",
            "lte" => literal is not null ? $"{expression} <= {literal}" : $"{text} <= {Parameter(value)}",
            _ => throw new FormatException($"'{filter.Op}' is not a filter"),
        };
    }

    /// Same qualification the change scripts use, kept here so the builder does not depend on the
    /// editing namespace: schema-qualified when the path carries a schema, bare otherwise.
    internal static string Qualify(SchemaNodeRef target, SqlDialect dialect) =>
        target.Path.Count > 1
            ? $"{dialect.QuoteIdentifier(target.Path[0])}.{dialect.QuoteIdentifier(target.Name)}"
            : dialect.QuoteIdentifier(target.Name);
}
