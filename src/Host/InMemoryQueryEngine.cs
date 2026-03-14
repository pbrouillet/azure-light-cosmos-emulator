using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class InMemoryQueryEngine : IQueryEngine
{
    public Task<FeedResponse<JsonObject>> ExecuteQueryAsync(
        string databaseId,
        string containerId,
        string query,
        IReadOnlyDictionary<string, object?>? parameters = null,
        QueryOptions? options = null,
        CancellationToken ct = default)
    {
        return Task.FromResult(new FeedResponse<JsonObject>
        {
            Resources = []
        });
    }
}
