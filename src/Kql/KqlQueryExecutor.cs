using Azure.Cosmos.LightEmulator.Kql.Operators;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;

namespace Azure.Cosmos.LightEmulator.Kql;

/// <summary>
/// Parses and executes KQL queries against data provided by a table resolver function.
/// </summary>
public class KqlQueryExecutor
{
    private readonly KqlSchemaRegistry _schemaRegistry;

    public KqlQueryExecutor(KqlSchemaRegistry schemaRegistry)
    {
        _schemaRegistry = schemaRegistry;
    }

    /// <summary>
    /// Executes a KQL query. The tableResolver maps table names to async row streams.
    /// </summary>
    public async Task<KqlQueryResult> ExecuteAsync(
        string kql,
        Func<string, IAsyncEnumerable<Dictionary<string, object?>>> tableResolver)
    {
        if (string.IsNullOrWhiteSpace(kql))
            throw new ArgumentException("Query text is required.", nameof(kql));

        var globalState = _schemaRegistry.GetGlobalState();
        var code = KustoCode.ParseAndAnalyze(kql, globalState);

        var diagnostics = code.GetDiagnostics()
            .Where(d => d.Severity == "Error")
            .Select(d => d.Message)
            .ToList();

        if (diagnostics.Count > 0)
            throw new KqlQueryException(diagnostics);

        var statement = FindExecutableStatement(code.Syntax);
        if (statement is null)
            throw new InvalidOperationException("No executable statement found in query.");

        var expression = statement is ExpressionStatement exprStmt ? exprStmt.Expression : statement;
        var pipeline = BuildPipeline(expression, tableResolver);

        var rows = new List<Dictionary<string, object?>>();
        await foreach (var row in pipeline)
        {
            rows.Add(row);
        }

        var schema = InferSchema(code, rows);
        return new KqlQueryResult(schema, rows);
    }

    private static SyntaxNode? FindExecutableStatement(SyntaxNode root)
    {
        if (root is ExpressionStatement)
            return root;

        for (int i = 0; i < root.ChildCount; i++)
        {
            var child = root.GetChild(i) as SyntaxNode;
            if (child is null) continue;

            if (child is ExpressionStatement)
                return child;

            var found = FindExecutableStatement(child);
            if (found is not null) return found;
        }

        return null;
    }

    private IAsyncEnumerable<Dictionary<string, object?>> BuildPipeline(
        SyntaxNode node,
        Func<string, IAsyncEnumerable<Dictionary<string, object?>>> tableResolver)
    {
        switch (node)
        {
            case PipeExpression pipe:
            {
                var source = BuildPipeline(pipe.Expression, tableResolver);
                var op = CreateOperator(pipe.Operator);
                return op.Execute(source);
            }

            case NameReference nameRef:
                return tableResolver(nameRef.SimpleName);

            case ExpressionStatement exprStmt:
                return BuildPipeline(exprStmt.Expression, tableResolver);

            default:
                throw new NotSupportedException($"Syntax node type '{node.GetType().Name}' is not supported.");
        }
    }

    private IKqlOperator CreateOperator(QueryOperator queryOp)
    {
        switch (queryOp)
        {
            case FilterOperator whereOp:
                return new WhereOp(whereOp.Condition);

            case ProjectOperator projectOp:
            {
                var columns = ParseNamedExpressions(projectOp.Expressions);
                return new ProjectOp(columns);
            }

            case ExtendOperator extendOp:
            {
                var columns = ParseNamedExpressions(extendOp.Expressions);
                return new ExtendOp(columns);
            }

            case SummarizeOperator summarizeOp:
            {
                var aggregates = ParseNamedExpressions(summarizeOp.Aggregates);
                var byColumns = summarizeOp.ByClause is not null
                    ? ParseNamedExpressions(summarizeOp.ByClause.Expressions)
                    : Array.Empty<(string, Expression)>();
                return new SummarizeOp(aggregates, byColumns);
            }

            case SortOperator sortOp:
            {
                var orderings = ParseOrderings(sortOp.Expressions);
                return new SortOp(orderings);
            }

            case TakeOperator takeOp:
            {
                var count = ExpressionEvaluator.ConvertToLong(
                    ExpressionEvaluator.Evaluate(takeOp.Expression, new Dictionary<string, object?>()));
                return new TakeOp(count);
            }

            case CountOperator:
                return new CountOp();

            case DistinctOperator distinctOp:
            {
                var columns = new List<string>();
                foreach (var sep in distinctOp.Expressions)
                {
                    if (sep.Element is NameReference nr)
                        columns.Add(nr.SimpleName);
                }
                return new DistinctOp(columns);
            }

            case TopOperator topOp:
            {
                var count = ExpressionEvaluator.ConvertToLong(
                    ExpressionEvaluator.Evaluate(topOp.Expression, new Dictionary<string, object?>()));
                var orderings = ParseTopByExpression(topOp.ByExpression);
                return new TopOp(count, orderings);
            }

            case ProjectAwayOperator projectAwayOp:
            {
                var columns = new List<string>();
                foreach (var sep in projectAwayOp.Expressions)
                    if (sep.Element is NameReference nr) columns.Add(nr.SimpleName);
                return new ProjectAwayOp(columns);
            }

            default:
                throw new NotSupportedException($"KQL operator '{queryOp.GetType().Name}' is not supported.");
        }
    }

    private static IReadOnlyList<(string Name, Expression Expr)> ParseNamedExpressions(SyntaxList<SeparatedElement<Expression>> expressions)
    {
        var result = new List<(string, Expression)>();
        foreach (var sep in expressions)
        {
            var expr = sep.Element;
            if (expr is SimpleNamedExpression named)
            {
                result.Add((named.Name.SimpleName, named.Expression));
            }
            else if (expr is FunctionCallExpression funcCall)
            {
                var name = funcCall.Name.SimpleName.ToLowerInvariant();
                if (name == "count") name = "count_";
                result.Add((name, funcCall));
            }
            else if (expr is NameReference nameRef)
            {
                result.Add((nameRef.SimpleName, nameRef));
            }
            else
            {
                result.Add((expr.ToString().Trim(), expr));
            }
        }
        return result;
    }

    private static IReadOnlyList<(string ColumnName, bool Ascending)> ParseOrderings(SyntaxList<SeparatedElement<Expression>> expressions)
    {
        var result = new List<(string, bool)>();
        foreach (var sep in expressions)
        {
            var expr = sep.Element;
            if (expr is OrderedExpression ordered)
            {
                var name = ordered.Expression is NameReference nr ? nr.SimpleName : ordered.Expression.ToString().Trim();
                var asc = ordered.Ordering?.AscOrDescKeyword.Kind == SyntaxKind.AscKeyword;
                result.Add((name, asc));
            }
            else if (expr is NameReference nameRef)
            {
                result.Add((nameRef.SimpleName, false)); // default desc in KQL
            }
        }
        return result;
    }

    private static IReadOnlyList<(string ColumnName, bool Ascending)> ParseTopByExpression(Expression byExpr)
    {
        if (byExpr is OrderedExpression ordered)
        {
            var name = ordered.Expression is NameReference nr ? nr.SimpleName : ordered.Expression.ToString().Trim();
            var asc = ordered.Ordering?.AscOrDescKeyword.Kind == SyntaxKind.AscKeyword;
            return [(name, asc)];
        }

        if (byExpr is NameReference nameRef)
        {
            return [(nameRef.SimpleName, false)]; // default desc
        }

        return [(byExpr.ToString().Trim(), false)];
    }

    private static KqlTableSchema InferSchema(KustoCode code, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        // Try to get schema from the Kusto semantic analysis
        var resultType = code.ResultType;
        if (resultType is TableSymbol tableSymbol)
        {
            var columns = tableSymbol.Columns
                .Select(c => new KqlColumnSchema(c.Name, MapType(c.Type)))
                .ToList();
            return new KqlTableSchema("result", columns);
        }

        // Fallback: infer from first row
        if (rows.Count > 0)
        {
            var columns = rows[0].Keys
                .Select(k => new KqlColumnSchema(k, InferColumnType(rows, k)))
                .ToList();
            return new KqlTableSchema("result", columns);
        }

        return new KqlTableSchema("result", []);
    }

    private static string MapType(TypeSymbol type)
    {
        if (type == ScalarTypes.String) return "string";
        if (type == ScalarTypes.Long) return "long";
        if (type == ScalarTypes.Int) return "int";
        if (type == ScalarTypes.Real) return "real";
        if (type == ScalarTypes.DateTime) return "datetime";
        if (type == ScalarTypes.TimeSpan) return "timespan";
        if (type == ScalarTypes.Bool) return "bool";
        if (type == ScalarTypes.Decimal) return "decimal";
        if (type == ScalarTypes.Guid) return "guid";
        if (type == ScalarTypes.Dynamic) return "dynamic";
        return "string";
    }

    private static string InferColumnType(IReadOnlyList<Dictionary<string, object?>> rows, string column)
    {
        var sample = rows.Select(r => r.GetValueOrDefault(column)).FirstOrDefault(v => v is not null);
        return sample switch
        {
            long => "long",
            int => "int",
            double => "real",
            bool => "bool",
            DateTimeOffset => "datetime",
            DateTime => "datetime",
            TimeSpan => "timespan",
            _ => "string"
        };
    }
}

/// <summary>
/// Operator that removes specified columns from rows.
/// </summary>
internal class ProjectAwayOp : IKqlOperator
{
    private readonly IReadOnlyList<string> _columns;

    public ProjectAwayOp(IReadOnlyList<string> columns)
    {
        _columns = columns;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        var removeSet = new HashSet<string>(_columns, StringComparer.OrdinalIgnoreCase);
        await foreach (var row in input)
        {
            var newRow = new Dictionary<string, object?>();
            foreach (var (key, value) in row)
            {
                if (!removeSet.Contains(key))
                    newRow[key] = value;
            }
            yield return newRow;
        }
    }
}

/// <summary>
/// Exception thrown when KQL parsing produces errors.
/// </summary>
public class KqlQueryException : Exception
{
    public IReadOnlyList<string> Diagnostics { get; }

    public KqlQueryException(IReadOnlyList<string> diagnostics)
        : base(string.Join("; ", diagnostics))
    {
        Diagnostics = diagnostics;
    }
}
