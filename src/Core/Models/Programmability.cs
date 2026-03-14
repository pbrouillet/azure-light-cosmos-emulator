namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB stored procedure resource.
/// </summary>
public class StoredProcedure
{
    public required string Id { get; set; }
    public string Rid { get; set; } = ResourceId.Generate();
    public string Self { get; set; } = string.Empty;
    public string ETag { get; set; } = ETagGenerator.Generate();
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public required string DatabaseId { get; set; }
    public required string ContainerId { get; set; }

    /// <summary>JavaScript function body.</summary>
    public required string Body { get; set; }
}

/// <summary>
/// Represents a Cosmos DB trigger resource.
/// </summary>
public class Trigger
{
    public required string Id { get; set; }
    public string Rid { get; set; } = ResourceId.Generate();
    public string Self { get; set; } = string.Empty;
    public string ETag { get; set; } = ETagGenerator.Generate();
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public required string DatabaseId { get; set; }
    public required string ContainerId { get; set; }

    /// <summary>JavaScript function body.</summary>
    public required string Body { get; set; }

    /// <summary>Trigger type: Pre or Post.</summary>
    public TriggerType TriggerType { get; set; }

    /// <summary>Trigger operation: All, Create, Replace, Delete.</summary>
    public TriggerOperation TriggerOperation { get; set; }
}

/// <summary>
/// Represents a Cosmos DB user-defined function resource.
/// </summary>
public class UserDefinedFunction
{
    public required string Id { get; set; }
    public string Rid { get; set; } = ResourceId.Generate();
    public string Self { get; set; } = string.Empty;
    public string ETag { get; set; } = ETagGenerator.Generate();
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public required string DatabaseId { get; set; }
    public required string ContainerId { get; set; }

    /// <summary>JavaScript function body.</summary>
    public required string Body { get; set; }
}

public enum TriggerType
{
    Pre,
    Post
}

public enum TriggerOperation
{
    All,
    Create,
    Replace,
    Delete
}
