using System.Collections.Concurrent;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Storage.ChangeFeed;

/// <summary>
/// In-memory change feed provider that tracks document changes.
/// </summary>
public class InMemoryChangeFeedProvider : IChangeFeedProvider
{
    private readonly ConcurrentDictionary<string, List<ChangeFeedItem>> _changeFeed = new();
    private readonly object _lock = new();

    public Task RecordChangeAsync(
        string databaseId,
        string containerId,
        CosmosDocument document,
        ChangeType changeType,
        CosmosDocument? previousImage = null,
        CancellationToken ct = default)
    {
        var key = $"{databaseId}/{containerId}";
        var item = new ChangeFeedItem
        {
            Document = document,
            Lsn = document.Lsn,
            ChangeType = changeType,
            PreviousImage = previousImage,
            Timestamp = DateTimeOffset.UtcNow
        };

        lock (_lock)
        {
            if (!_changeFeed.TryGetValue(key, out var items))
            {
                items = [];
                _changeFeed[key] = items;
            }
            items.Add(item);
        }

        return Task.CompletedTask;
    }

    public Task<FeedResponse<ChangeFeedItem>> ReadChangeFeedAsync(
        string databaseId,
        string containerId,
        ChangeFeedOptions options,
        CancellationToken ct = default)
    {
        var key = $"{databaseId}/{containerId}";

        if (!_changeFeed.TryGetValue(key, out var items))
        {
            return Task.FromResult(new FeedResponse<ChangeFeedItem>
            {
                Resources = [],
                ContinuationToken = "0"
            });
        }

        long startLsn = 0;

        if (!string.IsNullOrEmpty(options.ContinuationToken) && long.TryParse(options.ContinuationToken, out var parsedLsn))
        {
            startLsn = parsedLsn;
        }
        else if (options.StartTime.HasValue)
        {
            startLsn = items
                .Where(i => i.Timestamp >= options.StartTime.Value)
                .Select(i => i.Lsn)
                .DefaultIfEmpty(0)
                .Min();
        }
        else if (!options.StartFromBeginning)
        {
            startLsn = items.Count > 0 ? items[^1].Lsn : 0;
        }

        IEnumerable<ChangeFeedItem> filtered = items.Where(i => i.Lsn > startLsn);

        if (options.PartitionKey != null)
        {
            filtered = filtered.Where(i => i.Document.PartitionKey.Equals(options.PartitionKey));
        }

        if (!options.FullFidelity)
        {
            filtered = filtered.Where(i => i.ChangeType != ChangeType.Delete);
        }

        var maxItems = options.MaxItemCount ?? 100;
        var result = filtered.Take(maxItems).ToList();

        var lastLsn = result.Count > 0 ? result[^1].Lsn : startLsn;

        return Task.FromResult(new FeedResponse<ChangeFeedItem>
        {
            Resources = result,
            ContinuationToken = lastLsn.ToString()
        });
    }

    public Task TrimAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;
        lock (_lock)
        {
            foreach (var key in _changeFeed.Keys.ToList())
            {
                if (_changeFeed.TryGetValue(key, out var items))
                {
                    items.RemoveAll(i => i.Timestamp < cutoff);
                    if (items.Count == 0)
                        _changeFeed.TryRemove(key, out _);
                }
            }
        }

        return Task.CompletedTask;
    }
}
