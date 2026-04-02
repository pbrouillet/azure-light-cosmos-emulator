using System.Text.Json.Serialization;

namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// A single query telemetry record capturing execution metadata.
/// </summary>
public class QueryTelemetryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("databaseId")] public string DatabaseId { get; set; } = "";
    [JsonPropertyName("containerId")] public string ContainerId { get; set; } = "";
    [JsonPropertyName("sqlText")] public string SqlText { get; set; } = "";
    [JsonPropertyName("partitionKey")] public string? PartitionKey { get; set; }
    [JsonPropertyName("consistencyLevel")] public string ConsistencyLevel { get; set; } = "";
    [JsonPropertyName("requestCharge")] public double RequestCharge { get; set; }
    [JsonPropertyName("latencyMs")] public long LatencyMs { get; set; }
    [JsonPropertyName("itemCount")] public int ItemCount { get; set; }
    [JsonPropertyName("statusCode")] public int StatusCode { get; set; }
    [JsonPropertyName("activityId")] public string ActivityId { get; set; } = "";
    [JsonPropertyName("continuationToken")] public string? ContinuationToken { get; set; }
    [JsonPropertyName("isCrossPartition")] public bool IsCrossPartition { get; set; }
    [JsonPropertyName("queryPlan")] public string? QueryPlan { get; set; }
}
