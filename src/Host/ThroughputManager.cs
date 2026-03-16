using System.Collections.Concurrent;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class ThroughputManager
{
    private readonly ConcurrentDictionary<string, ContainerBudget> _budgets = new(StringComparer.OrdinalIgnoreCase);

    public bool TryConsume(string databaseId, string containerId, int provisionedRuPerSecond, double requestCharge, out int retryAfterMs)
    {
        var key = $"{databaseId}/{containerId}";
        var budget = _budgets.GetOrAdd(key, static _ => new ContainerBudget());
        return budget.TryConsume(Math.Max(1, provisionedRuPerSecond), Math.Max(0.1, requestCharge), out retryAfterMs);
    }

    public bool TryConsumeDatabase(string databaseId, int provisionedRuPerSecond, double requestCharge, out int retryAfterMs)
    {
        var key = $"db:{databaseId}";
        var budget = _budgets.GetOrAdd(key, static _ => new ContainerBudget());
        return budget.TryConsume(Math.Max(1, provisionedRuPerSecond), Math.Max(0.1, requestCharge), out retryAfterMs);
    }

    private sealed class ContainerBudget
    {
        private readonly Queue<(long Second, double Charge)> _buckets = new();
        private readonly object _syncRoot = new();
        private double _consumed;

        public bool TryConsume(int limit, double charge, out int retryAfterMs)
        {
            lock (_syncRoot)
            {
                var now = DateTimeOffset.UtcNow;
                var currentSecond = now.ToUnixTimeSeconds();
                TrimExpiredBuckets(currentSecond);

                if (_consumed + charge > limit)
                {
                    retryAfterMs = Math.Max(100, 1000 - now.Millisecond);
                    return false;
                }

                if (_buckets.Count > 0 && _buckets.TryPeek(out var existing) && existing.Second == currentSecond)
                {
                    _buckets.Dequeue();
                    _buckets.Enqueue((existing.Second, existing.Charge + charge));
                }
                else
                {
                    _buckets.Enqueue((currentSecond, charge));
                }

                _consumed += charge;
                retryAfterMs = 0;
                return true;
            }
        }

        private void TrimExpiredBuckets(long currentSecond)
        {
            while (_buckets.Count > 0 && _buckets.Peek().Second < currentSecond)
            {
                _consumed -= _buckets.Dequeue().Charge;
            }

            if (_consumed < 0)
            {
                _consumed = 0;
            }
        }
    }
}
