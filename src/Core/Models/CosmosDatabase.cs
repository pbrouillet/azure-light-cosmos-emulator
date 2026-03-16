namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB database resource.
/// </summary>
public class CosmosDatabase
{
    /// <summary>User-provided unique identifier.</summary>
    public required string Id { get; set; }

    /// <summary>System-generated resource ID.</summary>
    public string Rid { get; set; } = ResourceId.Generate();

    /// <summary>Self-link URI.</summary>
    public string Self => $"dbs/{Rid}/";

    /// <summary>ETag for optimistic concurrency.</summary>
    public string ETag { get; set; } = ETagGenerator.Generate();

    /// <summary>Last modified timestamp (Unix epoch seconds).</summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Provisioned throughput in request units per second (null = unlimited).</summary>
    public int? MaxThroughput { get; set; }

    /// <summary>Collections link.</summary>
    public string Colls => $"colls/";

    /// <summary>Users link.</summary>
    public string Users => $"users/";
}
