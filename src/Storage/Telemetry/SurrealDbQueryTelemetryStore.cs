using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.Storage.Telemetry;

internal sealed class DbQueryTelemetryRecord
{
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
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("queryPlan")] public string? QueryPlan { get; set; }
}

/// <summary>
/// SurrealDB-backed query telemetry store.
/// </summary>
public class SurrealDbQueryTelemetryStore : IQueryTelemetryStore
{
    private const string TelemetryTable = "cosmos_query_telemetry";
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();

    private readonly SurrealDbConnectionManager _connectionManager;

    public SurrealDbQueryTelemetryStore(SurrealDbConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task RecordAsync(QueryTelemetryEntry entry, CancellationToken ct = default)
    {
        var record = new DbQueryTelemetryRecord
        {
            DatabaseId = entry.DatabaseId,
            ContainerId = entry.ContainerId,
            SqlText = entry.SqlText,
            PartitionKey = entry.PartitionKey,
            ConsistencyLevel = entry.ConsistencyLevel,
            RequestCharge = entry.RequestCharge,
            LatencyMs = entry.LatencyMs,
            ItemCount = entry.ItemCount,
            StatusCode = entry.StatusCode,
            ActivityId = entry.ActivityId,
            ContinuationToken = entry.ContinuationToken,
            IsCrossPartition = entry.IsCrossPartition,
            Timestamp = entry.Timestamp.ToUnixTimeMilliseconds(),
            QueryPlan = entry.QueryPlan
        };

        var recordKey = entry.Id;
        await ExecuteAsync(
            "CREATE $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(TelemetryTable, recordKey),
                ["data"] = record
            },
            ct);
    }

    public async Task<IReadOnlyList<QueryTelemetryEntry>> ListAsync(
        string? databaseId = null,
        string? containerId = null,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        var sql = $"SELECT * FROM {TelemetryTable} ORDER BY timestamp DESC";
        var records = await QueryRecordsAsync<DbQueryTelemetryRecord>(sql, null, ct);

        IEnumerable<DbQueryTelemetryRecord> filtered = records;

        if (!string.IsNullOrEmpty(databaseId))
            filtered = filtered.Where(r => string.Equals(r.DatabaseId, databaseId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(containerId))
            filtered = filtered.Where(r => string.Equals(r.ContainerId, containerId, StringComparison.OrdinalIgnoreCase));

        return filtered.Take(maxItems).Select(r => new QueryTelemetryEntry
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.Timestamp),
            DatabaseId = r.DatabaseId,
            ContainerId = r.ContainerId,
            SqlText = r.SqlText,
            PartitionKey = r.PartitionKey,
            ConsistencyLevel = r.ConsistencyLevel,
            RequestCharge = r.RequestCharge,
            LatencyMs = r.LatencyMs,
            ItemCount = r.ItemCount,
            StatusCode = r.StatusCode,
            ActivityId = r.ActivityId,
            ContinuationToken = r.ContinuationToken,
            IsCrossPartition = r.IsCrossPartition,
            QueryPlan = r.QueryPlan
        }).ToList();
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await ExecuteAsync($"DELETE {TelemetryTable}", null, ct);
    }

    private async Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var response = await client.RawQuery(sql, parameters ?? EmptyParameters, ct);
        response.EnsureAllOks();
    }

    private async Task<List<T>> QueryRecordsAsync<T>(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var response = await client.RawQuery(sql, parameters ?? EmptyParameters, ct);
        response.EnsureAllOks();
        return response.GetValues<T>(0).ToList();
    }

    private async Task<ISurrealDbClient> GetClientAsync(CancellationToken ct)
    {
        await _connectionManager.InitializeAsync(ct);
        return _connectionManager.Client;
    }
}
