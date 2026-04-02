using Kusto.Language.Syntax;

namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class ExtendOp : IKqlOperator
{
    private readonly IReadOnlyList<(string Name, Expression Expr)> _columns;

    public ExtendOp(IReadOnlyList<(string Name, Expression Expr)> columns)
    {
        _columns = columns;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        await foreach (var row in input)
        {
            var newRow = new Dictionary<string, object?>(row);
            foreach (var (name, expr) in _columns)
            {
                newRow[name] = ExpressionEvaluator.Evaluate(expr, row);
            }
            yield return newRow;
        }
    }
}
