namespace Azure.Cosmos.LightEmulator.Kql;

/// <summary>
/// A single operator in a KQL pipeline that transforms a stream of rows.
/// </summary>
public interface IKqlOperator
{
    IAsyncEnumerable<Dictionary<string, object?>> Execute(IAsyncEnumerable<Dictionary<string, object?>> input);
}
