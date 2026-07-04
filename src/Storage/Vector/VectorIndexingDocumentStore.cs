using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Storage.Vector;

/// <summary>
/// <see cref="IDocumentStore"/> decorator that keeps the vector index in sync with
/// document mutations. All storage is delegated to the wrapped inner store; only the
/// document- and container-mutating operations additionally notify the
/// <see cref="IVectorIndexProvider"/>. This works for any backing store (Sqlite,
/// InMemory, SurrealDb).
/// </summary>
public sealed class VectorIndexingDocumentStore : IDocumentStore
{
    private readonly IDocumentStore _inner;
    private readonly IVectorIndexProvider _index;

    public VectorIndexingDocumentStore(IDocumentStore inner, IVectorIndexProvider index)
    {
        _inner = inner;
        _index = index;
    }

    // ---- Databases (pass-through) ----
    public Task<CosmosDatabase> CreateDatabaseAsync(string id, CancellationToken ct = default) =>
        _inner.CreateDatabaseAsync(id, ct);
    public Task<CosmosDatabase> GetDatabaseAsync(string id, CancellationToken ct = default) =>
        _inner.GetDatabaseAsync(id, ct);
    public Task<FeedResponse<CosmosDatabase>> ListDatabasesAsync(CancellationToken ct = default) =>
        _inner.ListDatabasesAsync(ct);
    public Task<CosmosDatabase> ReplaceDatabaseAsync(CosmosDatabase database, CancellationToken ct = default) =>
        _inner.ReplaceDatabaseAsync(database, ct);
    public Task DeleteDatabaseAsync(string id, CancellationToken ct = default) =>
        _inner.DeleteDatabaseAsync(id, ct);

    // ---- Containers ----
    public Task<CosmosContainer> CreateContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default) =>
        _inner.CreateContainerAsync(databaseId, container, ct);
    public Task<CosmosContainer> GetContainerAsync(string databaseId, string containerId, CancellationToken ct = default) =>
        _inner.GetContainerAsync(databaseId, containerId, ct);
    public Task<FeedResponse<CosmosContainer>> ListContainersAsync(string databaseId, CancellationToken ct = default) =>
        _inner.ListContainersAsync(databaseId, ct);

    public async Task<CosmosContainer> ReplaceContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default)
    {
        var result = await _inner.ReplaceContainerAsync(databaseId, container, ct).ConfigureAwait(false);
        // Indexing/embedding policy may have changed; drop shards so they rebuild lazily.
        _index.OnContainerDropped(databaseId, container.Id);
        return result;
    }

    public async Task DeleteContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        await _inner.DeleteContainerAsync(databaseId, containerId, ct).ConfigureAwait(false);
        _index.OnContainerDropped(databaseId, containerId);
    }

    // ---- Documents ----
    public async Task<CosmosDocument> CreateDocumentAsync(string databaseId, string containerId, JsonObject document, bool? isIndexed = null, CancellationToken ct = default)
    {
        var doc = await _inner.CreateDocumentAsync(databaseId, containerId, document, isIndexed, ct).ConfigureAwait(false);
        _index.OnUpsert(databaseId, containerId, doc);
        return doc;
    }

    public Task<CosmosDocument> ReadDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default) =>
        _inner.ReadDocumentAsync(databaseId, containerId, documentId, partitionKey, ct);

    public async Task<CosmosDocument> ReplaceDocumentAsync(string databaseId, string containerId, string documentId, JsonObject document, string? ifMatch = null, bool? isIndexed = null, CancellationToken ct = default)
    {
        var doc = await _inner.ReplaceDocumentAsync(databaseId, containerId, documentId, document, ifMatch, isIndexed, ct).ConfigureAwait(false);
        _index.OnUpsert(databaseId, containerId, doc);
        return doc;
    }

    public async Task<CosmosDocument> UpsertDocumentAsync(string databaseId, string containerId, JsonObject document, bool? isIndexed = null, CancellationToken ct = default)
    {
        var doc = await _inner.UpsertDocumentAsync(databaseId, containerId, document, isIndexed, ct).ConfigureAwait(false);
        _index.OnUpsert(databaseId, containerId, doc);
        return doc;
    }

    public async Task<CosmosDocument> PatchDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, IReadOnlyList<PatchOperation> operations, string? ifMatch = null, string? condition = null, CancellationToken ct = default)
    {
        var doc = await _inner.PatchDocumentAsync(databaseId, containerId, documentId, partitionKey, operations, ifMatch, condition, ct).ConfigureAwait(false);
        _index.OnUpsert(databaseId, containerId, doc);
        return doc;
    }

    public async Task DeleteDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        await _inner.DeleteDocumentAsync(databaseId, containerId, documentId, partitionKey, ct).ConfigureAwait(false);
        _index.OnDelete(databaseId, containerId, documentId, partitionKey);
    }

    public async Task<int> EmptyContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var count = await _inner.EmptyContainerAsync(databaseId, containerId, ct).ConfigureAwait(false);
        _index.OnContainerCleared(databaseId, containerId);
        return count;
    }

    public Task<long> GetGlobalLsnAsync(CancellationToken ct = default) => _inner.GetGlobalLsnAsync(ct);

    // ---- Batch ----
    public async Task<IReadOnlyList<BatchOperationResponse>> ExecuteBatchAsync(
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        IReadOnlyList<BatchOperationRequest> operations,
        CancellationToken ct = default)
    {
        var responses = await _inner.ExecuteBatchAsync(databaseId, containerId, partitionKey, operations, ct).ConfigureAwait(false);

        for (var i = 0; i < operations.Count && i < responses.Count; i++)
        {
            var op = operations[i];
            var response = responses[i];
            if (response.StatusCode is < 200 or >= 300)
            {
                continue;
            }

            switch (op.OperationType)
            {
                case BatchOperationType.Create:
                case BatchOperationType.Replace:
                case BatchOperationType.Upsert:
                case BatchOperationType.Patch:
                    var id = op.Id ?? response.ResourceBody?["id"]?.GetValue<string>();
                    if (id is not null)
                    {
                        var doc = await _inner.ReadDocumentAsync(databaseId, containerId, id, partitionKey, ct).ConfigureAwait(false);
                        _index.OnUpsert(databaseId, containerId, doc);
                    }

                    break;
                case BatchOperationType.Delete:
                    if (op.Id is not null)
                    {
                        _index.OnDelete(databaseId, containerId, op.Id, partitionKey);
                    }

                    break;
            }
        }

        return responses;
    }

    // ---- Bulk reads (pass-through) ----
    public Task<FeedResponse<CosmosDocument>> ReadManyDocumentsAsync(string databaseId, string containerId, IEnumerable<(string id, PartitionKeyValue pk)> items, CancellationToken ct = default) =>
        _inner.ReadManyDocumentsAsync(databaseId, containerId, items, ct);
    public Task<FeedResponse<CosmosDocument>> ListDocumentsAsync(string databaseId, string containerId, CancellationToken ct = default) =>
        _inner.ListDocumentsAsync(databaseId, containerId, ct);

    // ---- Users (pass-through) ----
    public Task<CosmosUser> CreateUserAsync(string databaseId, string userId, CancellationToken ct = default) =>
        _inner.CreateUserAsync(databaseId, userId, ct);
    public Task<CosmosUser> GetUserAsync(string databaseId, string userId, CancellationToken ct = default) =>
        _inner.GetUserAsync(databaseId, userId, ct);
    public Task<FeedResponse<CosmosUser>> ListUsersAsync(string databaseId, CancellationToken ct = default) =>
        _inner.ListUsersAsync(databaseId, ct);
    public Task<CosmosUser> ReplaceUserAsync(string databaseId, CosmosUser user, CancellationToken ct = default) =>
        _inner.ReplaceUserAsync(databaseId, user, ct);
    public Task DeleteUserAsync(string databaseId, string userId, CancellationToken ct = default) =>
        _inner.DeleteUserAsync(databaseId, userId, ct);

    // ---- Permissions (pass-through) ----
    public Task<CosmosPermission> CreatePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default) =>
        _inner.CreatePermissionAsync(databaseId, userId, permission, ct);
    public Task<CosmosPermission> GetPermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default) =>
        _inner.GetPermissionAsync(databaseId, userId, permissionId, ct);
    public Task<FeedResponse<CosmosPermission>> ListPermissionsAsync(string databaseId, string userId, CancellationToken ct = default) =>
        _inner.ListPermissionsAsync(databaseId, userId, ct);
    public Task<CosmosPermission> ReplacePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default) =>
        _inner.ReplacePermissionAsync(databaseId, userId, permission, ct);
    public Task DeletePermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default) =>
        _inner.DeletePermissionAsync(databaseId, userId, permissionId, ct);

    // ---- Offers (pass-through) ----
    public Task<CosmosOffer> GetOfferAsync(string offerId, CancellationToken ct = default) =>
        _inner.GetOfferAsync(offerId, ct);
    public Task<FeedResponse<CosmosOffer>> ListOffersAsync(CancellationToken ct = default) =>
        _inner.ListOffersAsync(ct);
    public Task<CosmosOffer> ReplaceOfferAsync(CosmosOffer offer, CancellationToken ct = default) =>
        _inner.ReplaceOfferAsync(offer, ct);
}
