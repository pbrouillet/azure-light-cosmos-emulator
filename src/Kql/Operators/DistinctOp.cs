namespace Azure.Cosmos.LightEmulator.Kql.Operators;

public class DistinctOp : IKqlOperator
{
    private readonly IReadOnlyList<string> _columns;

    public DistinctOp(IReadOnlyList<string> columns)
    {
        _columns = columns;
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input)
    {
        var seen = new HashSet<string>();
        await foreach (var row in input)
        {
            var key = string.Join("|", _columns.Select(c =>
                ExpressionEvaluator.ConvertToString(row.GetValueOrDefault(c)) ?? "(null)"));

            if (seen.Add(key))
            {
                if (_columns.Count > 0)
                {
                    var newRow = new Dictionary<string, object?>(_columns.Count);
                    foreach (var col in _columns)
                        newRow[col] = row.GetValueOrDefault(col);
                    yield return newRow;
                }
                else
                {
                    yield return row;
                }
            }
        }
    }
}
