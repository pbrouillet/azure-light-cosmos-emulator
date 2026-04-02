namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class TakeOp : IKqlOperator
{
    private readonly long _count;

    public TakeOp(long count)
    {
        _count = count;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        long emitted = 0;
        await foreach (var row in input)
        {
            if (emitted >= _count) yield break;
            yield return row;
            emitted++;
        }
    }
}
