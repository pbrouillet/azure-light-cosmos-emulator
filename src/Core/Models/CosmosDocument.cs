using System.Text.Json;
using System.Text.Json.Nodes;

namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a Cosmos DB document resource.
/// </summary>
public class CosmosDocument
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

    /// <summary>Attachments link.</summary>
    public string Attachments => $"attachments/";

    /// <summary>Parent database ID.</summary>
    public required string DatabaseId { get; set; }

    /// <summary>Parent container ID.</summary>
    public required string ContainerId { get; set; }

    /// <summary>Partition key value(s).</summary>
    public required PartitionKeyValue PartitionKey { get; set; }

    /// <summary>The document body as a JSON object.</summary>
    public required JsonObject Body { get; set; }

    /// <summary>Time to live in seconds (null = use container default).</summary>
    public int? TimeToLive { get; set; }

    /// <summary>Logical sequence number for change feed ordering.</summary>
    public long Lsn { get; set; }

    /// <summary>Whether this document is included in query indexes (affected by x-ms-indexing-directive).</summary>
    public bool IsIndexed { get; set; } = true;

    /// <summary>
    /// Merges system properties into the body for serialization.
    /// </summary>
    public JsonObject ToResponseBody()
    {
        var result = Body.DeepClone().AsObject();
        result["id"] = Id;
        result["_rid"] = Rid;
        result["_self"] = Self;
        result["_etag"] = ETag;
        result["_ts"] = Timestamp;
        result["_attachments"] = Attachments;
        return result;
    }
}
