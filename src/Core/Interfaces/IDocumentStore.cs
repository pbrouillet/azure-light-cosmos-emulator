using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Core storage abstraction for Cosmos DB document operations.
/// </summary>
public interface IDocumentStore
{
    // Database operations
    Task<CosmosDatabase> CreateDatabaseAsync(string id, CancellationToken ct = default);
    Task<CosmosDatabase> GetDatabaseAsync(string id, CancellationToken ct = default);
    Task<FeedResponse<CosmosDatabase>> ListDatabasesAsync(CancellationToken ct = default);
    Task DeleteDatabaseAsync(string id, CancellationToken ct = default);

    // Container operations
    Task<CosmosContainer> CreateContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default);
    Task<CosmosContainer> GetContainerAsync(string databaseId, string containerId, CancellationToken ct = default);
    Task<FeedResponse<CosmosContainer>> ListContainersAsync(string databaseId, CancellationToken ct = default);
    Task<CosmosContainer> ReplaceContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default);
    Task DeleteContainerAsync(string databaseId, string containerId, CancellationToken ct = default);

    // Document operations
    Task<CosmosDocument> CreateDocumentAsync(string databaseId, string containerId, JsonObject document, CancellationToken ct = default);
    Task<CosmosDocument> ReadDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default);
    Task<CosmosDocument> ReplaceDocumentAsync(string databaseId, string containerId, string documentId, JsonObject document, string? ifMatch = null, CancellationToken ct = default);
    Task<CosmosDocument> UpsertDocumentAsync(string databaseId, string containerId, JsonObject document, CancellationToken ct = default);
    Task DeleteDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default);

    // Bulk operations
    Task<FeedResponse<CosmosDocument>> ReadManyDocumentsAsync(string databaseId, string containerId, IEnumerable<(string id, PartitionKeyValue pk)> items, CancellationToken ct = default);
    Task<FeedResponse<CosmosDocument>> ListDocumentsAsync(string databaseId, string containerId, CancellationToken ct = default);
}
