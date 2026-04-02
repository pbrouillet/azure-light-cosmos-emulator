using Kusto.Language.Syntax;

namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class ProjectOp : IKqlOperator
{
    private readonly IReadOnlyList<(string OutputName, Expression Expr)> _columns;

    public ProjectOp(IReadOnlyList<(string OutputName, Expression Expr)> columns)
    {
        _columns = columns;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        await foreach (var row in input)
        {
            var newRow = new Dictionary<string, object?>(_columns.Count);
            foreach (var (name, expr) in _columns)
            {
                newRow[name] = ExpressionEvaluator.Evaluate(expr, row);
            }
            yield return newRow;
        }
    }
}
