using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Persistent store for query telemetry data.
/// </summary>
public interface IQueryTelemetryStore
{
    /// <summary>
    /// Records a query telemetry entry.
    /// </summary>
    Task RecordAsync(QueryTelemetryEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Lists query telemetry entries, optionally filtered by database and container.
    /// </summary>
    Task<IReadOnlyList<QueryTelemetryEntry>> ListAsync(
        string? databaseId = null,
        string? containerId = null,
        int maxItems = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Clears all query telemetry entries.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);
}
