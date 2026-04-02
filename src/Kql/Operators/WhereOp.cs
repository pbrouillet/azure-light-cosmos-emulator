using Kusto.Language.Syntax;

namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class WhereOp : IKqlOperator
{
    private readonly Expression _predicate;

    public WhereOp(Expression predicate)
    {
        _predicate = predicate;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        await foreach (var row in input)
        {
            var result = ExpressionEvaluator.Evaluate(_predicate, row);
            if (result is true)
                yield return row;
        }
    }
}
