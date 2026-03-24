namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB permission resource.
/// </summary>
public class CosmosPermission
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

    /// <summary>Parent user ID.</summary>
    public required string UserId { get; set; }

    /// <summary>Access mode: All or Read.</summary>
    public required PermissionMode PermissionMode { get; set; }

    /// <summary>Full addressable path of the resource (e.g., dbs/db1/colls/coll1/).</summary>
    public required string Resource { get; set; }

    /// <summary>System-generated resource token.</summary>
    public string? Token { get; set; }
}

/// <summary>
/// Permission access mode.
/// </summary>
public enum PermissionMode
{
    /// <summary>Read-only access.</summary>
    Read,

    /// <summary>Full access (read, write, delete).</summary>
    All
}
