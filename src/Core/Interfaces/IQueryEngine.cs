using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Cosmos DB SQL query engine abstraction.
/// </summary>
public interface IQueryEngine
{
    /// <summary>
    /// Executes a Cosmos SQL query against a container.
    /// </summary>
    /// <param name="databaseId">Database identifier.</param>
    /// <param name="containerId">Container identifier.</param>
    /// <param name="query">SQL query text.</param>
    /// <param name="parameters">Query parameters.</param>
    /// <param name="options">Query execution options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Feed response with matching documents.</returns>
    Task<FeedResponse<JsonObject>> ExecuteQueryAsync(
        string databaseId,
        string containerId,
        string query,
        IReadOnlyDictionary<string, object?>? parameters = null,
        QueryOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>
/// Options for query execution.
/// </summary>
public class QueryOptions
{
    /// <summary>Maximum number of items to return.</summary>
    public int? MaxItemCount { get; set; }

    /// <summary>Continuation token for pagination.</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Enable cross-partition queries.</summary>
    public bool EnableCrossPartitionQuery { get; set; }

    /// <summary>Partition key for single-partition queries.</summary>
    public PartitionKeyValue? PartitionKey { get; set; }

    /// <summary>Enable index scan for queries on excluded paths or IndexingMode.None.</summary>
    public bool EnableScan { get; set; }
}
