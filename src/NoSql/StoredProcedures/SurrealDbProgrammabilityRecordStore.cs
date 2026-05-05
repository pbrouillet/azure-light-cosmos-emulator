using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;

/// <summary>
/// SurrealDB-backed implementation of IProgrammabilityRecordStore.
/// </summary>
public sealed class SurrealDbProgrammabilityRecordStore : IProgrammabilityRecordStore
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();
    private readonly SurrealDbConnectionManager _connectionManager;

    public SurrealDbProgrammabilityRecordStore(SurrealDbConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task<T?> SelectRecordAsync<T>(string table, string recordKey, CancellationToken ct) where T : class
    {
        var client = await GetClientAsync(ct);
        return await client.Select<T>(new RecordIdOfString(table, recordKey), ct);
    }

    public async Task<List<T>> SelectTableRecordsAsync<T>(string table, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        return (await client.Select<T>(table, ct)).ToList();
    }

    public async Task CreateRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var response = await client.RawQuery(
            "CREATE $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(table, recordKey),
                ["data"] = record
            },
            ct);
        response.EnsureAllOks();
    }

    public async Task UpsertRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var response = await client.RawQuery(
            "UPSERT $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(table, recordKey),
                ["data"] = record
            },
            ct);
        response.EnsureAllOks();
    }

    public async Task DeleteRecordAsync(string table, string recordKey, string resourceType, string resourceId, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var deleted = await client.Delete(new RecordIdOfString(table, recordKey), ct);
        if (!deleted)
        {
            throw CosmosEmulatorException.NotFound(resourceType, resourceId);
        }
    }

    private async Task<ISurrealDbClient> GetClientAsync(CancellationToken ct)
    {
        await _connectionManager.InitializeAsync(ct);
        return _connectionManager.Client;
    }
}
