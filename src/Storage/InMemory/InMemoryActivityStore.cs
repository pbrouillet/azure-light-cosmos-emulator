using System.Collections.Concurrent;
using Azure.Cosmos.LightEmulator.Core.Interfaces;

namespace Azure.Cosmos.LightEmulator.Storage.InMemory;

public class InMemoryActivityStore : IActivityStore
{
    private readonly ConcurrentBag<ActivityEntry> _entries = new();

    public Task RecordAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActivityEntry>> ListAsync(int maxItems = 1000, CancellationToken ct = default)
    {
        var result = _entries
            .OrderByDescending(e => e.Timestamp)
            .Take(maxItems)
            .ToList();
        return Task.FromResult<IReadOnlyList<ActivityEntry>>(result);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        return Task.CompletedTask;
    }
}
