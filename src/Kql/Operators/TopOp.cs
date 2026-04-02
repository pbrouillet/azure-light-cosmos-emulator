namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class TopOp : IKqlOperator
{
    private readonly long _count;
    private readonly IReadOnlyList<(string ColumnName, bool Ascending)> _orderings;

    public TopOp(long count, IReadOnlyList<(string ColumnName, bool Ascending)> orderings)
    {
        _count = count;
        _orderings = orderings;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        var rows = new List<Dictionary<string, object?>>();
        await foreach (var row in input)
        {
            rows.Add(row);
        }

        rows.Sort((a, b) =>
        {
            foreach (var (col, asc) in _orderings)
            {
                var va = a.GetValueOrDefault(col);
                var vb = b.GetValueOrDefault(col);
                var cmp = ExpressionEvaluator.CompareValues(va, vb);
                if (cmp != 0) return asc ? cmp : -cmp;
            }
            return 0;
        });

        long emitted = 0;
        foreach (var row in rows)
        {
            if (emitted >= _count) yield break;
            yield return row;
            emitted++;
        }
    }
}
