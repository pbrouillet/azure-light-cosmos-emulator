namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB container (collection) resource.
/// </summary>
public class CosmosContainer
{
    /// <summary>User-provided unique identifier.</summary>
    public required string Id { get; set; }

    /// <summary>System-generated resource ID.</summary>
    public string Rid { get; set; } = ResourceId.Generate();

    /// <summary>Self-link URI.</summary>
    public string Self { get; set; } = string.Empty;

    /// <summary>ETag for optimistic concurrency.</summary>
    public string ETag { get; set; } = ETagGenerator.Generate();

    /// <summary>Last modified timestamp (Unix epoch seconds).</summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Parent database ID.</summary>
    public required string DatabaseId { get; set; }

    /// <summary>Partition key definition.</summary>
    public required PartitionKeyDefinition PartitionKey { get; set; }

    /// <summary>Indexing policy.</summary>
    public IndexingPolicy IndexingPolicy { get; set; } = new();

    /// <summary>Default time to live in seconds (-1 = off, 0 = default, >0 = TTL).</summary>
    public int? DefaultTimeToLive { get; set; }

    /// <summary>Unique key policy.</summary>
    public UniqueKeyPolicy? UniqueKeyPolicy { get; set; }

    /// <summary>Conflict resolution policy.</summary>
    public ConflictResolutionPolicy? ConflictResolutionPolicy { get; set; }
}
