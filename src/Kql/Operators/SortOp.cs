namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class SortOp : IKqlOperator
{
    private readonly IReadOnlyList<(string ColumnName, bool Ascending)> _orderings;

    public SortOp(IReadOnlyList<(string ColumnName, bool Ascending)> orderings)
    {
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

        foreach (var row in rows)
        {
            yield return row;
        }
    }
}
