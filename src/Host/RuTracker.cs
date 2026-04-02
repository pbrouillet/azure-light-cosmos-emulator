using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;

namespace Azure.Cosmos.LightEmulator.Host;

public class ActivityLogEntry
{
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("statusCode")] public int StatusCode { get; set; }
    [JsonPropertyName("requestCharge")] public double RequestCharge { get; set; }
    [JsonPropertyName("latencyMs")] public double LatencyMs { get; set; }
    [JsonPropertyName("databaseId")] public string? DatabaseId { get; set; }
    [JsonPropertyName("containerId")] public string? ContainerId { get; set; }
}

public sealed class RuTracker
{
    private const int MaxRecentActivity = 200;

    private readonly object _syncRoot = new();
    private readonly ConcurrentQueue<ActivityLogEntry> _recentActivity = new();
    private readonly ConcurrentDictionary<string, double> _containerRu = new(StringComparer.OrdinalIgnoreCase);
    private long _totalRequests;
    private double _totalRu;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private IActivityStore? _activityStore;

    public void SetActivityStore(IActivityStore activityStore)
    {
        _activityStore = activityStore;
    }

    public void RecordRequest(
        double ru,
        string? method = null,
        string? path = null,
        int? statusCode = null,
        double? latencyMs = null,
        string? databaseId = null,
        string? containerId = null)
    {
        Interlocked.Increment(ref _totalRequests);
        lock (_syncRoot)
        {
            _totalRu += ru;
        }

        if (!string.IsNullOrEmpty(databaseId) && !string.IsNullOrEmpty(containerId))
        {
            var key = $"{databaseId}/{containerId}";
            _containerRu.AddOrUpdate(key, ru, (_, existingRu) => existingRu + ru);
        }

        _recentActivity.Enqueue(new ActivityLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Method = method ?? string.Empty,
            Path = path ?? string.Empty,
            StatusCode = statusCode ?? 0,
            RequestCharge = ru,
            LatencyMs = latencyMs ?? 0,
            DatabaseId = databaseId,
            ContainerId = containerId
        });

        while (_recentActivity.Count > MaxRecentActivity && _recentActivity.TryDequeue(out _))
        {
        }

        // Fire-and-forget persist to SurrealDB
        if (_activityStore is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _activityStore.RecordAsync(new ActivityEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Method = method ?? string.Empty,
                        Path = path ?? string.Empty,
                        StatusCode = statusCode ?? 0,
                        RequestCharge = ru,
                        LatencyMs = latencyMs ?? 0,
                        DatabaseId = databaseId,
                        ContainerId = containerId
                    });
                }
                catch
                {
                    // Best-effort persistence — don't fail the request pipeline
                }
            });
        }
    }

    public long TotalRequests => Interlocked.Read(ref _totalRequests);

    public double TotalRequestUnits
    {
        get
        {
            lock (_syncRoot)
            {
                return _totalRu;
            }
        }
    }

    public IReadOnlyList<ActivityLogEntry> GetRecentActivity() => _recentActivity.Reverse().ToArray();

    public IReadOnlyDictionary<string, double> GetContainerRu() => new Dictionary<string, double>(_containerRu);

    public double UptimeSeconds => (DateTimeOffset.UtcNow - _startTime).TotalSeconds;
}
