using System.Collections.Concurrent;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Storage.InMemory;

public class InMemoryQueryTelemetryStore : IQueryTelemetryStore
{
    private readonly ConcurrentBag<QueryTelemetryEntry> _entries = new();

    public Task RecordAsync(QueryTelemetryEntry entry, CancellationToken ct = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueryTelemetryEntry>> ListAsync(
        string? databaseId = null,
        string? containerId = null,
        int maxItems = 100,
        CancellationToken ct = default)
    {
        var query = _entries.AsEnumerable();
        if (!string.IsNullOrEmpty(databaseId))
            query = query.Where(e => string.Equals(e.DatabaseId, databaseId, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(containerId))
            query = query.Where(e => string.Equals(e.ContainerId, containerId, StringComparison.Ordinal));
        var result = query.OrderByDescending(e => e.Timestamp).Take(maxItems).ToList();
        return Task.FromResult<IReadOnlyList<QueryTelemetryEntry>>(result);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        return Task.CompletedTask;
    }
}
