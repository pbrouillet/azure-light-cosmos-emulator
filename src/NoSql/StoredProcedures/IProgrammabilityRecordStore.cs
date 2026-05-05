using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Cosmos.LightEmulator.Core.Exceptions;

namespace Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;

/// <summary>
/// Abstraction for the low-level record storage used by JintProgrammabilityEngine.
/// Allows the programmability engine to work with any storage backend.
/// </summary>
public interface IProgrammabilityRecordStore
{
    Task<T?> SelectRecordAsync<T>(string table, string recordKey, CancellationToken ct) where T : class;
    Task<List<T>> SelectTableRecordsAsync<T>(string table, CancellationToken ct);
    Task CreateRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct);
    Task UpsertRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct);
    Task DeleteRecordAsync(string table, string recordKey, string resourceType, string resourceId, CancellationToken ct);
}

/// <summary>
/// In-memory implementation of IProgrammabilityRecordStore using ConcurrentDictionary.
/// Used when the SurrealDB backend is not active.
/// </summary>
public sealed class InMemoryProgrammabilityRecordStore : IProgrammabilityRecordStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolverChain = { new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }
    };

    // Key: "table:recordKey", Value: serialized JSON
    private readonly ConcurrentDictionary<string, string> _records = new(StringComparer.Ordinal);

    private static string MakeKey(string table, string recordKey) => $"{table}:{recordKey}";

    public Task<T?> SelectRecordAsync<T>(string table, string recordKey, CancellationToken ct) where T : class
    {
        if (_records.TryGetValue(MakeKey(table, recordKey), out var json))
        {
            return Task.FromResult(JsonSerializer.Deserialize<T>(json, JsonOptions));
        }
        return Task.FromResult<T?>(null);
    }

    public Task<List<T>> SelectTableRecordsAsync<T>(string table, CancellationToken ct)
    {
        var prefix = $"{table}:";
        var results = _records
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kvp => JsonSerializer.Deserialize<T>(kvp.Value, JsonOptions)!)
            .ToList();
        return Task.FromResult(results);
    }

    public Task CreateRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        _records[MakeKey(table, recordKey)] = json;
        return Task.CompletedTask;
    }

    public Task UpsertRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        _records[MakeKey(table, recordKey)] = json;
        return Task.CompletedTask;
    }

    public Task DeleteRecordAsync(string table, string recordKey, string resourceType, string resourceId, CancellationToken ct)
    {
        if (!_records.TryRemove(MakeKey(table, recordKey), out _))
        {
            throw CosmosEmulatorException.NotFound(resourceType, resourceId);
        }
        return Task.CompletedTask;
    }
}
