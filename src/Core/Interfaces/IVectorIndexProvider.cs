using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Maintains approximate/exact vector indexes for container embedding paths and
/// serves nearest-neighbour queries. Implementations are expected to be
/// thread-safe. Maintenance methods (<see cref="OnUpsert"/>, <see cref="OnDelete"/>,
/// etc.) are invoked by the storage layer as documents change.
/// </summary>
public interface IVectorIndexProvider
{
    /// <summary>Whether index-accelerated vector search is enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Ensures a vector index shard exists for the given container path, building
    /// it from existing documents on first use. Returns <c>false</c> when indexing
    /// is disabled or the path cannot be indexed (e.g. no declared policy and
    /// implicit indexing is off).
    /// </summary>
    Task<bool> EnsureIndexAsync(
        string databaseId,
        string containerId,
        string path,
        string indexType,
        VectorDistanceFunction distanceFunction,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the nearest neighbours for the request, ordered nearest-first.
    /// Documents lacking a valid embedding at the path are excluded.
    /// </summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(VectorSearchRequest request, CancellationToken ct = default);

    /// <summary>Records a document insert/update in all shards for its container.</summary>
    void OnUpsert(string databaseId, string containerId, CosmosDocument document);

    /// <summary>Records a document deletion in all shards for its container.</summary>
    void OnDelete(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey);

    /// <summary>Clears all shard contents for a container (e.g. after emptying it).</summary>
    void OnContainerCleared(string databaseId, string containerId);

    /// <summary>Drops all shards for a container (e.g. after deleting it).</summary>
    void OnContainerDropped(string databaseId, string containerId);
}
