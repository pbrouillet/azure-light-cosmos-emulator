namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// A request to find the nearest neighbours of a query vector within a container.
/// </summary>
public sealed record VectorSearchRequest
{
    /// <summary>Database identifier.</summary>
    public required string DatabaseId { get; init; }

    /// <summary>Container identifier.</summary>
    public required string ContainerId { get; init; }

    /// <summary>The document property path holding the embedding, e.g. <c>/embedding</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The query vector.</summary>
    public required float[] QueryVector { get; init; }

    /// <summary>The distance function to rank by.</summary>
    public required VectorDistanceFunction DistanceFunction { get; init; }

    /// <summary>Number of nearest neighbours to return.</summary>
    public required int TopK { get; init; }

    /// <summary>Optional partition-key scope; when set, only that partition is searched.</summary>
    public PartitionKeyValue? PartitionKey { get; init; }

    /// <summary>
    /// The vector index type: <c>flat</c> (exact), <c>quantizedFlat</c> or
    /// <c>diskANN</c> (HNSW approximate).
    /// </summary>
    public string IndexType { get; init; } = "diskANN";
}

/// <summary>
/// A single nearest-neighbour result: the document identity plus its
/// nearest-first distance (lower is closer) and Cosmos <c>VectorDistance</c> score.
/// </summary>
public sealed record VectorHit(string DocumentId, PartitionKeyValue PartitionKey, double Distance, double Score);
