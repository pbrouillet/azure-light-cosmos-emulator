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
    Task<CosmosDatabase> ReplaceDatabaseAsync(CosmosDatabase database, CancellationToken ct = default);
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
    Task<CosmosDocument> PatchDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, IReadOnlyList<PatchOperation> operations, string? ifMatch = null, CancellationToken ct = default);
    Task DeleteDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default);
    Task<long> GetGlobalLsnAsync(CancellationToken ct = default);

    // Bulk operations
    Task<FeedResponse<CosmosDocument>> ReadManyDocumentsAsync(string databaseId, string containerId, IEnumerable<(string id, PartitionKeyValue pk)> items, CancellationToken ct = default);
    Task<FeedResponse<CosmosDocument>> ListDocumentsAsync(string databaseId, string containerId, CancellationToken ct = default);

    // User operations
    Task<CosmosUser> CreateUserAsync(string databaseId, string userId, CancellationToken ct = default);
    Task<CosmosUser> GetUserAsync(string databaseId, string userId, CancellationToken ct = default);
    Task<FeedResponse<CosmosUser>> ListUsersAsync(string databaseId, CancellationToken ct = default);
    Task<CosmosUser> ReplaceUserAsync(string databaseId, CosmosUser user, CancellationToken ct = default);
    Task DeleteUserAsync(string databaseId, string userId, CancellationToken ct = default);

    // Permission operations
    Task<CosmosPermission> CreatePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default);
    Task<CosmosPermission> GetPermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default);
    Task<FeedResponse<CosmosPermission>> ListPermissionsAsync(string databaseId, string userId, CancellationToken ct = default);
    Task<CosmosPermission> ReplacePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default);
    Task DeletePermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default);

    // Offer operations
    Task<CosmosOffer> GetOfferAsync(string offerId, CancellationToken ct = default);
    Task<FeedResponse<CosmosOffer>> ListOffersAsync(CancellationToken ct = default);
    Task<CosmosOffer> ReplaceOfferAsync(CosmosOffer offer, CancellationToken ct = default);
}
