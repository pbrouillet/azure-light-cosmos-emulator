using Kusto.Language.Syntax;

namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class SummarizeOp : IKqlOperator
{
    private readonly IReadOnlyList<(string OutputName, Expression AggExpr)> _aggregates;
    private readonly IReadOnlyList<(string OutputName, Expression Expr)> _byColumns;

    public SummarizeOp(
        IReadOnlyList<(string OutputName, Expression AggExpr)> aggregates,
        IReadOnlyList<(string OutputName, Expression Expr)> byColumns)
    {
        _aggregates = aggregates;
        _byColumns = byColumns;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        var allRows = new List<Dictionary<string, object?>>();
        await foreach (var row in input)
        {
            allRows.Add(row);
        }

        if (_byColumns.Count == 0)
        {
            var result = new Dictionary<string, object?>();
            foreach (var (name, aggExpr) in _aggregates)
            {
                result[name] = EvaluateAggregate(allRows, aggExpr);
            }
            yield return result;
        }
        else
        {
            var groups = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
            var groupKeys = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var row in allRows)
            {
                var keyParts = new string[_byColumns.Count];
                var keyValues = new Dictionary<string, object?>(_byColumns.Count);

                for (int i = 0; i < _byColumns.Count; i++)
                {
                    var val = ExpressionEvaluator.Evaluate(_byColumns[i].Expr, row);
                    keyValues[_byColumns[i].OutputName] = val;
                    keyParts[i] = ExpressionEvaluator.ConvertToString(val) ?? "(null)";
                }

                var groupKey = string.Join("|", keyParts);
                if (!groups.TryGetValue(groupKey, out var group))
                {
                    group = new List<Dictionary<string, object?>>();
                    groups[groupKey] = group;
                    groupKeys[groupKey] = keyValues;
                }
                group.Add(row);
            }

            foreach (var (key, group) in groups)
            {
                var result = new Dictionary<string, object?>(groupKeys[key]);
                foreach (var (name, aggExpr) in _aggregates)
                {
                    result[name] = EvaluateAggregate(group, aggExpr);
                }
                yield return result;
            }
        }
    }

    private static object? EvaluateAggregate(List<Dictionary<string, object?>> group, Expression aggExpr)
    {
        if (aggExpr is FunctionCallExpression funcExpr)
        {
            var funcName = funcExpr.Name.SimpleName.ToLowerInvariant();
            var argExprs = funcExpr.ArgumentList.Expressions
                .Select(e => e.Element)
                .ToList();

            switch (funcName)
            {
                case "count":
                    return (long)group.Count;

                case "sum":
                    return group.Sum(r => ExpressionEvaluator.ConvertToDouble(
                        ExpressionEvaluator.Evaluate(argExprs[0], r)));

                case "avg":
                {
                    if (group.Count == 0) return null;
                    var sum = group.Sum(r => ExpressionEvaluator.ConvertToDouble(
                        ExpressionEvaluator.Evaluate(argExprs[0], r)));
                    return sum / group.Count;
                }

                case "min":
                {
                    object? minVal = null;
                    foreach (var r in group)
                    {
                        var val = ExpressionEvaluator.Evaluate(argExprs[0], r);
                        if (val is null) continue;
                        if (minVal is null || ExpressionEvaluator.CompareValues(val, minVal) < 0)
                            minVal = val;
                    }
                    return minVal;
                }

                case "max":
                {
                    object? maxVal = null;
                    foreach (var r in group)
                    {
                        var val = ExpressionEvaluator.Evaluate(argExprs[0], r);
                        if (val is null) continue;
                        if (maxVal is null || ExpressionEvaluator.CompareValues(val, maxVal) > 0)
                            maxVal = val;
                    }
                    return maxVal;
                }

                case "dcount":
                case "count_distinct":
                {
                    var set = new HashSet<string?>();
                    foreach (var r in group)
                    {
                        var val = ExpressionEvaluator.Evaluate(argExprs[0], r);
                        set.Add(ExpressionEvaluator.ConvertToString(val));
                    }
                    return (long)set.Count;
                }

                case "countif":
                {
                    long count = 0;
                    foreach (var r in group)
                    {
                        var val = ExpressionEvaluator.Evaluate(argExprs[0], r);
                        if (val is true) count++;
                    }
                    return count;
                }

                case "sumif":
                {
                    double sum = 0;
                    foreach (var r in group)
                    {
                        var pred = ExpressionEvaluator.Evaluate(argExprs[1], r);
                        if (pred is true)
                            sum += ExpressionEvaluator.ConvertToDouble(
                                ExpressionEvaluator.Evaluate(argExprs[0], r));
                    }
                    return sum;
                }

                case "avgif":
                {
                    double sum = 0;
                    long count = 0;
                    foreach (var r in group)
                    {
                        var pred = ExpressionEvaluator.Evaluate(argExprs[1], r);
                        if (pred is true)
                        {
                            sum += ExpressionEvaluator.ConvertToDouble(
                                ExpressionEvaluator.Evaluate(argExprs[0], r));
                            count++;
                        }
                    }
                    return count == 0 ? null : sum / count;
                }

                case "make_list":
                {
                    return group.Select(r => ExpressionEvaluator.Evaluate(argExprs[0], r)).ToList<object?>();
                }

                case "make_set":
                {
                    var seen = new HashSet<string?>();
                    var list = new List<object?>();
                    foreach (var r in group)
                    {
                        var val = ExpressionEvaluator.Evaluate(argExprs[0], r);
                        var key = ExpressionEvaluator.ConvertToString(val);
                        if (seen.Add(key))
                            list.Add(val);
                    }
                    return list;
                }

                case "any":
                case "take_any":
                {
                    foreach (var r in group)
                    {
                        var val = ExpressionEvaluator.Evaluate(argExprs[0], r);
                        if (val is not null) return val;
                    }
                    return null;
                }

                case "percentile":
                {
                    var values = group.Select(r => ExpressionEvaluator.ConvertToDouble(
                        ExpressionEvaluator.Evaluate(argExprs[0], r)))
                        .OrderBy(x => x).ToList();
                    var p = ExpressionEvaluator.ConvertToDouble(
                        ExpressionEvaluator.Evaluate(argExprs[1], group[0]));
                    if (values.Count == 0) return null;
                    var n = (p / 100.0) * (values.Count - 1);
                    var lower = (int)Math.Floor(n);
                    var upper = (int)Math.Ceiling(n);
                    if (lower == upper || upper >= values.Count) return values[lower];
                    return values[lower] + (n - lower) * (values[upper] - values[lower]);
                }

                default:
                    throw new NotSupportedException($"Aggregate function '{funcName}' is not supported.");
            }
        }

        // If not a function, evaluate as scalar on first row
        if (group.Count > 0)
            return ExpressionEvaluator.Evaluate(aggExpr, group[0]);

        return null;
    }
}
