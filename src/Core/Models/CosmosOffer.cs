namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB offer resource (throughput configuration).
/// </summary>
public class CosmosOffer
{
    /// <summary>System-generated offer ID (same as Rid).</summary>
    public string Id { get; set; } = ResourceId.Generate();

    /// <summary>System-generated resource ID.</summary>
    public string Rid { get; set; } = string.Empty;

    /// <summary>Self-link URI.</summary>
    public string Self => $"offers/{Id}/";

    /// <summary>ETag for optimistic concurrency.</summary>
    public string ETag { get; set; } = ETagGenerator.Generate();

    /// <summary>Last modified timestamp (Unix epoch seconds).</summary>
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Offer version (V2 for RU-based throughput).</summary>
    public string OfferVersion { get; set; } = "V2";

    /// <summary>Offer type (Invalid for V2 offers).</summary>
    public string OfferType { get; set; } = "Invalid";

    /// <summary>Throughput configuration.</summary>
    public required OfferContent Content { get; set; }

    /// <summary>Self-link of the associated collection.</summary>
    public required string Resource { get; set; }

    /// <summary>Resource ID (_rid) of the associated collection.</summary>
    public required string OfferResourceId { get; set; }
}

/// <summary>
/// Offer content containing throughput settings.
/// </summary>
public class OfferContent
{
    /// <summary>Provisioned throughput in RU/s.</summary>
    public int OfferThroughput { get; set; }
}
