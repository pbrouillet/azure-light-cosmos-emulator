using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.Storage.Telemetry;

internal sealed class DbActivityRecord
{
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("statusCode")] public int StatusCode { get; set; }
    [JsonPropertyName("requestCharge")] public double RequestCharge { get; set; }
    [JsonPropertyName("latencyMs")] public double LatencyMs { get; set; }
    [JsonPropertyName("databaseId")] public string? DatabaseId { get; set; }
    [JsonPropertyName("containerId")] public string? ContainerId { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
}

/// <summary>
/// SurrealDB-backed activity log store.
/// </summary>
public class SurrealDbActivityStore : IActivityStore
{
    private const string ActivityTable = "cosmos_activity_log";
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();

    private readonly SurrealDbConnectionManager _connectionManager;

    public SurrealDbActivityStore(SurrealDbConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task RecordAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        var record = new DbActivityRecord
        {
            Method = entry.Method,
            Path = entry.Path,
            StatusCode = entry.StatusCode,
            RequestCharge = entry.RequestCharge,
            LatencyMs = entry.LatencyMs,
            DatabaseId = entry.DatabaseId,
            ContainerId = entry.ContainerId,
            Timestamp = entry.Timestamp.ToUnixTimeMilliseconds()
        };

        var recordKey = Guid.NewGuid().ToString("N");
        await ExecuteAsync(
            "CREATE $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(ActivityTable, recordKey),
                ["data"] = record
            },
            ct);
    }

    public async Task<IReadOnlyList<ActivityEntry>> ListAsync(int maxItems = 1000, CancellationToken ct = default)
    {
        var sql = $"SELECT * FROM {ActivityTable} ORDER BY timestamp DESC LIMIT {maxItems}";
        var records = await QueryRecordsAsync<DbActivityRecord>(sql, null, ct);

        return records.Select(r => new ActivityEntry
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.Timestamp),
            Method = r.Method,
            Path = r.Path,
            StatusCode = r.StatusCode,
            RequestCharge = r.RequestCharge,
            LatencyMs = r.LatencyMs,
            DatabaseId = r.DatabaseId,
            ContainerId = r.ContainerId
        }).ToList();
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await ExecuteAsync($"DELETE {ActivityTable}", null, ct);
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
