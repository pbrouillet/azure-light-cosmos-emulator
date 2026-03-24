namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB user resource.
/// </summary>
public class CosmosUser
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

    /// <summary>Permissions link.</summary>
    public string Permissions => "permissions/";
}
