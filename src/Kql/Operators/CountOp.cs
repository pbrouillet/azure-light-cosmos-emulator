namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class CountOp : IKqlOperator
{
    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        long count = 0;
        await foreach (var _ in input)
        {
            count++;
        }
        yield return new Dictionary<string, object?> { ["Count"] = count };
    }
}
