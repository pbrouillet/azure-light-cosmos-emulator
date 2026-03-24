namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB feed (list) response.
/// </summary>
/// <typeparam name="T">The resource type.</typeparam>
public class FeedResponse<T>
{
    /// <summary>Resource ID of the containing resource.</summary>
    public string Rid { get; set; } = string.Empty;

    /// <summary>The number of items returned.</summary>
    public int Count => Resources.Count;

    /// <summary>The returned resources.</summary>
    public required List<T> Resources { get; set; }

    /// <summary>Continuation token for pagination.</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Request charge in RUs.</summary>
    public double RequestCharge { get; set; } = 1.0;

    /// <summary>Activity ID for request tracing.</summary>
    public string ActivityId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Session token.</summary>
    public string? SessionToken { get; set; }

    /// <summary>RU cost multiplier applied when a scan was required.</summary>
    public double RuMultiplier { get; set; } = 1.0;
}
