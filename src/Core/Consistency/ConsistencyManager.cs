using System.Collections.Concurrent;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Consistency;

/// <summary>
/// Default implementation of consistency level management.
/// </summary>
public class ConsistencyManager : Interfaces.IConsistencyManager
{
    private readonly ConsistencyLevel _defaultLevel;
    private readonly ConcurrentDictionary<string, long> _containerLsns = new();

    public ConsistencyManager(ConsistencyLevel defaultLevel = ConsistencyLevel.Session)
    {
        _defaultLevel = defaultLevel;
    }

    public ConsistencyLevel DefaultConsistencyLevel => _defaultLevel;

    public bool IsValidConsistencyLevel(ConsistencyLevel requested)
    {
        // Clients can request same or weaker consistency than default
        return requested >= _defaultLevel;
    }

    public ConsistencyLevel GetEffectiveConsistency(ConsistencyLevel? requested)
    {
        if (requested is null)
            return _defaultLevel;

        if (!IsValidConsistencyLevel(requested.Value))
            return _defaultLevel;

        return requested.Value;
    }

    public string GenerateSessionToken(string databaseId, string containerId, long lsn)
    {
        var key = $"{databaseId}/{containerId}";
        _containerLsns.AddOrUpdate(key, lsn, (_, existing) => Math.Max(existing, lsn));
        // Format: "0:lsn" — partition index : logical sequence number
        return $"0:{lsn}";
    }

    public bool ValidateSessionToken(string databaseId, string containerId, string? sessionToken)
    {
        if (string.IsNullOrEmpty(sessionToken))
            return true; // No session token means no session consistency requirement

        var key = $"{databaseId}/{containerId}";
        if (!_containerLsns.TryGetValue(key, out var currentLsn))
            return true; // Container not yet seen, accept any token

        if (!TryParseLsn(sessionToken, out var requestedLsn))
            return false;

        return requestedLsn <= currentLsn;
    }

    public string GetCurrentSessionToken(string databaseId, string containerId)
    {
        var key = $"{databaseId}/{containerId}";
        var lsn = _containerLsns.GetOrAdd(key, 0);
        return $"0:{lsn}";
    }

    private static bool TryParseLsn(string sessionToken, out long lsn)
    {
        lsn = 0;
        var parts = sessionToken.Split(':');
        return parts.Length == 2 && long.TryParse(parts[1], out lsn);
    }
}
