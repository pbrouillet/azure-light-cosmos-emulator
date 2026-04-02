namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Persistent store for HTTP request activity log entries.
/// </summary>
public interface IActivityStore
{
    /// <summary>
    /// Records an activity log entry.
    /// </summary>
    Task RecordAsync(ActivityEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Lists activity log entries, ordered by timestamp descending.
    /// </summary>
    Task<IReadOnlyList<ActivityEntry>> ListAsync(int maxItems = 1000, CancellationToken ct = default);

    /// <summary>
    /// Clears all activity log entries.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// A single HTTP request activity record for persistent storage.
/// </summary>
public class ActivityEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int StatusCode { get; set; }
    public double RequestCharge { get; set; }
    public double LatencyMs { get; set; }
    public string? DatabaseId { get; set; }
    public string? ContainerId { get; set; }
}
