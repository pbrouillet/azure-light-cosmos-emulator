using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Kql;

namespace Azure.Cosmos.LightEmulator.Host.Kql;

/// <summary>
/// Adapts IActivityStore and IQueryTelemetryStore data into row streams for the KQL engine.
/// </summary>
public class MonitoringStorageAdapter
{
    private readonly IActivityStore _activityStore;
    private readonly IQueryTelemetryStore _telemetryStore;

    public MonitoringStorageAdapter(IActivityStore activityStore, IQueryTelemetryStore telemetryStore)
    {
        _activityStore = activityStore;
        _telemetryStore = telemetryStore;
    }

    public IAsyncEnumerable<Dictionary<string, object?>> ResolveTable(string tableName)
    {
        return tableName.ToLowerInvariant() switch
        {
            "activity" => ScanActivity(),
            "telemetry" => ScanTelemetry(),
            _ => throw new InvalidOperationException(
                $"Unknown table '{tableName}'. Available tables: activity, telemetry")
        };
    }

    private async IAsyncEnumerable<Dictionary<string, object?>> ScanActivity()
    {
        var entries = await _activityStore.ListAsync(10000);
        foreach (var entry in entries)
        {
            yield return new Dictionary<string, object?>
            {
                ["timestamp"] = entry.Timestamp,
                ["method"] = entry.Method,
                ["path"] = entry.Path,
                ["statusCode"] = (long)entry.StatusCode,
                ["requestCharge"] = entry.RequestCharge,
                ["latencyMs"] = entry.LatencyMs,
                ["databaseId"] = entry.DatabaseId,
                ["containerId"] = entry.ContainerId,
            };
        }
    }

    private async IAsyncEnumerable<Dictionary<string, object?>> ScanTelemetry()
    {
        var entries = await _telemetryStore.ListAsync(maxItems: 10000);
        foreach (var entry in entries)
        {
            yield return new Dictionary<string, object?>
            {
                ["timestamp"] = entry.Timestamp,
                ["databaseId"] = entry.DatabaseId,
                ["containerId"] = entry.ContainerId,
                ["sqlText"] = entry.SqlText,
                ["partitionKey"] = entry.PartitionKey,
                ["consistencyLevel"] = entry.ConsistencyLevel,
                ["requestCharge"] = entry.RequestCharge,
                ["latencyMs"] = entry.LatencyMs,
                ["itemCount"] = (long)entry.ItemCount,
                ["statusCode"] = (long)entry.StatusCode,
                ["activityId"] = entry.ActivityId,
                ["isCrossPartition"] = entry.IsCrossPartition,
            };
        }
    }
}
