using System.Text;
using System.Text.Json.Serialization;
using Acornima;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using Jint;
using Jint.Native;
using Jint.Runtime;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;

internal sealed class DbStoredProcedureRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("databaseId")]
    public string DatabaseId { get; set; } = string.Empty;

    [JsonPropertyName("containerId")]
    public string ContainerId { get; set; } = string.Empty;

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("eTag")]
    public string ETag { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

internal sealed class DbTriggerRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("databaseId")]
    public string DatabaseId { get; set; } = string.Empty;

    [JsonPropertyName("containerId")]
    public string ContainerId { get; set; } = string.Empty;

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("eTag")]
    public string ETag { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("triggerType")]
    public int TriggerType { get; set; }

    [JsonPropertyName("triggerOperation")]
    public int TriggerOperation { get; set; }
}

internal sealed class DbUserDefinedFunctionRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("databaseId")]
    public string DatabaseId { get; set; } = string.Empty;

    [JsonPropertyName("containerId")]
    public string ContainerId { get; set; } = string.Empty;

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("eTag")]
    public string ETag { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// Persistent implementation of IProgrammabilityEngine using Jint JavaScript interpreter.
/// </summary>
public class JintProgrammabilityEngine : IProgrammabilityEngine
{
    private const string StoredProcedureTable = "cosmos_sprocs";
    private const string TriggerTable = "cosmos_triggers";
    private const string UdfTable = "cosmos_udfs";
    private static readonly TimeSpan SprocTimeout = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();

    private readonly IDocumentStore _store;
    private readonly IQueryEngine _queryEngine;
    private readonly SurrealDbConnectionManager _connectionManager;

    public JintProgrammabilityEngine(IDocumentStore store, IQueryEngine queryEngine, SurrealDbConnectionManager connectionManager)
    {
        _store = store;
        _queryEngine = queryEngine;
        _connectionManager = connectionManager;
    }

    public async Task<StoredProcedure> CreateStoredProcedureAsync(string databaseId, string containerId, StoredProcedure sproc, CancellationToken ct = default)
    {
        var recordKey = MakeRecordKey(databaseId, containerId, sproc.Id);
        if (await SelectRecordAsync<DbStoredProcedureRecord>(StoredProcedureTable, recordKey, ct) is not null)
        {
            throw CosmosEmulatorException.Conflict("StoredProcedure", sproc.Id);
        }

        sproc.DatabaseId = databaseId;
        sproc.ContainerId = containerId;
        sproc.Self = $"dbs/{databaseId}/colls/{containerId}/sprocs/{sproc.Id}/";

        await CreateRecordAsync(StoredProcedureTable, recordKey, ToRecord(sproc), ct);
        return sproc;
    }

    public async Task<StoredProcedure> GetStoredProcedureAsync(string databaseId, string containerId, string sprocId, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbStoredProcedureRecord>(StoredProcedureTable, MakeRecordKey(databaseId, containerId, sprocId), "StoredProcedure", sprocId, ct);
        return ToStoredProcedure(record);
    }

    public async Task<FeedResponse<StoredProcedure>> ListStoredProceduresAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var records = (await SelectTableRecordsAsync<DbStoredProcedureRecord>(StoredProcedureTable, ct))
            .Where(record => string.Equals(record.DatabaseId, databaseId, StringComparison.Ordinal)
                && string.Equals(record.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(record => record.Id, StringComparer.Ordinal)
            .ToList();

        return new FeedResponse<StoredProcedure>
        {
            Resources = records.Select(ToStoredProcedure).ToList()
        };
    }

    public async Task<StoredProcedure> ReplaceStoredProcedureAsync(string databaseId, string containerId, StoredProcedure sproc, CancellationToken ct = default)
    {
        var recordKey = MakeRecordKey(databaseId, containerId, sproc.Id);
        var existing = await GetRequiredRecordAsync<DbStoredProcedureRecord>(StoredProcedureTable, recordKey, "StoredProcedure", sproc.Id, ct);

        var updated = new StoredProcedure
        {
            Id = sproc.Id,
            DatabaseId = databaseId,
            ContainerId = containerId,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/colls/{containerId}/sprocs/{sproc.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Body = sproc.Body
        };

        await UpsertRecordAsync(StoredProcedureTable, recordKey, ToRecord(updated), ct);
        return updated;
    }

    public async Task DeleteStoredProcedureAsync(string databaseId, string containerId, string sprocId, CancellationToken ct = default)
    {
        await DeleteRecordAsync(StoredProcedureTable, MakeRecordKey(databaseId, containerId, sprocId), "StoredProcedure", sprocId, ct);
    }

    public async Task<object?> ExecuteStoredProcedureAsync(string databaseId, string containerId, string sprocId, object?[] args, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        var sproc = await GetStoredProcedureAsync(databaseId, containerId, sprocId, ct);
        var engine = new Engine(options => options.TimeoutInterval(SprocTimeout));
        var context = new CosmosJsContext(_store, _queryEngine, databaseId, containerId, partitionKey, ct);
        context.Bind(engine);

        engine.SetValue("getContext", new Func<CosmosJsContext>(() => context.getContext()));
        engine.SetValue("__args", JsValue.FromObject(engine, args));

        try
        {
            engine.Execute($"var __sprocFn = {sproc.Body}; __sprocFn.apply(null, __args);");
            return context.getResponse().getBody();
        }
        catch (CosmosEmulatorException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw CosmosEmulatorException.RequestTimeout(
                $"Stored procedure '{sprocId}' execution exceeded the maximum allowed time of {SprocTimeout.TotalSeconds} seconds.");
        }
        catch (Exception ex) when (IsJintException(ex))
        {
            throw CosmosEmulatorException.BadRequest($"Stored procedure execution failed: {FormatExecutionError(ex)}");
        }
        catch (Exception ex)
        {
            throw CosmosEmulatorException.BadRequest($"Stored procedure execution failed: {ex.Message}");
        }
    }

    public async Task<Trigger> CreateTriggerAsync(string databaseId, string containerId, Trigger trigger, CancellationToken ct = default)
    {
        var recordKey = MakeRecordKey(databaseId, containerId, trigger.Id);
        if (await SelectRecordAsync<DbTriggerRecord>(TriggerTable, recordKey, ct) is not null)
        {
            throw CosmosEmulatorException.Conflict("Trigger", trigger.Id);
        }

        trigger.DatabaseId = databaseId;
        trigger.ContainerId = containerId;
        trigger.Self = $"dbs/{databaseId}/colls/{containerId}/triggers/{trigger.Id}/";

        await CreateRecordAsync(TriggerTable, recordKey, ToRecord(trigger), ct);
        return trigger;
    }

    public async Task<Trigger> GetTriggerAsync(string databaseId, string containerId, string triggerId, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbTriggerRecord>(TriggerTable, MakeRecordKey(databaseId, containerId, triggerId), "Trigger", triggerId, ct);
        return ToTrigger(record);
    }

    public async Task<FeedResponse<Trigger>> ListTriggersAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var records = (await SelectTableRecordsAsync<DbTriggerRecord>(TriggerTable, ct))
            .Where(record => string.Equals(record.DatabaseId, databaseId, StringComparison.Ordinal)
                && string.Equals(record.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(record => record.Id, StringComparer.Ordinal)
            .ToList();

        return new FeedResponse<Trigger>
        {
            Resources = records.Select(ToTrigger).ToList()
        };
    }

    public async Task<Trigger> ReplaceTriggerAsync(string databaseId, string containerId, Trigger trigger, CancellationToken ct = default)
    {
        var recordKey = MakeRecordKey(databaseId, containerId, trigger.Id);
        var existing = await GetRequiredRecordAsync<DbTriggerRecord>(TriggerTable, recordKey, "Trigger", trigger.Id, ct);

        var updated = new Trigger
        {
            Id = trigger.Id,
            DatabaseId = databaseId,
            ContainerId = containerId,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/colls/{containerId}/triggers/{trigger.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Body = trigger.Body,
            TriggerType = trigger.TriggerType,
            TriggerOperation = trigger.TriggerOperation
        };

        await UpsertRecordAsync(TriggerTable, recordKey, ToRecord(updated), ct);
        return updated;
    }

    public async Task DeleteTriggerAsync(string databaseId, string containerId, string triggerId, CancellationToken ct = default)
    {
        await DeleteRecordAsync(TriggerTable, MakeRecordKey(databaseId, containerId, triggerId), "Trigger", triggerId, ct);
    }

    public async Task<UserDefinedFunction> CreateUdfAsync(string databaseId, string containerId, UserDefinedFunction udf, CancellationToken ct = default)
    {
        var recordKey = MakeRecordKey(databaseId, containerId, udf.Id);
        if (await SelectRecordAsync<DbUserDefinedFunctionRecord>(UdfTable, recordKey, ct) is not null)
        {
            throw CosmosEmulatorException.Conflict("UserDefinedFunction", udf.Id);
        }

        udf.DatabaseId = databaseId;
        udf.ContainerId = containerId;
        udf.Self = $"dbs/{databaseId}/colls/{containerId}/udfs/{udf.Id}/";

        await CreateRecordAsync(UdfTable, recordKey, ToRecord(udf), ct);
        return udf;
    }

    public async Task<UserDefinedFunction> GetUdfAsync(string databaseId, string containerId, string udfId, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbUserDefinedFunctionRecord>(UdfTable, MakeRecordKey(databaseId, containerId, udfId), "UserDefinedFunction", udfId, ct);
        return ToUserDefinedFunction(record);
    }

    public async Task<FeedResponse<UserDefinedFunction>> ListUdfsAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var records = (await SelectTableRecordsAsync<DbUserDefinedFunctionRecord>(UdfTable, ct))
            .Where(record => string.Equals(record.DatabaseId, databaseId, StringComparison.Ordinal)
                && string.Equals(record.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(record => record.Id, StringComparer.Ordinal)
            .ToList();

        return new FeedResponse<UserDefinedFunction>
        {
            Resources = records.Select(ToUserDefinedFunction).ToList()
        };
    }

    public async Task<UserDefinedFunction> ReplaceUdfAsync(string databaseId, string containerId, UserDefinedFunction udf, CancellationToken ct = default)
    {
        var recordKey = MakeRecordKey(databaseId, containerId, udf.Id);
        var existing = await GetRequiredRecordAsync<DbUserDefinedFunctionRecord>(UdfTable, recordKey, "UserDefinedFunction", udf.Id, ct);

        var updated = new UserDefinedFunction
        {
            Id = udf.Id,
            DatabaseId = databaseId,
            ContainerId = containerId,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/colls/{containerId}/udfs/{udf.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Body = udf.Body
        };

        await UpsertRecordAsync(UdfTable, recordKey, ToRecord(updated), ct);
        return updated;
    }

    public async Task DeleteUdfAsync(string databaseId, string containerId, string udfId, CancellationToken ct = default)
    {
        await DeleteRecordAsync(UdfTable, MakeRecordKey(databaseId, containerId, udfId), "UserDefinedFunction", udfId, ct);
    }

    private async Task<List<T>> QueryRecordsAsync<T>(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var response = await client.RawQuery(sql, parameters ?? EmptyParameters, ct);
        response.EnsureAllOks();
        return response.GetValues<T>(0).ToList();
    }

    private async Task<List<T>> SelectTableRecordsAsync<T>(string table, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        return (await client.Select<T>(table, ct)).ToList();
    }

    private async Task<ISurrealDbClient> GetClientAsync(CancellationToken ct)
    {
        await _connectionManager.InitializeAsync(ct);
        return _connectionManager.Client;
    }

    private async Task<T?> SelectRecordAsync<T>(string table, string recordKey, CancellationToken ct)
        where T : class
    {
        var client = await GetClientAsync(ct);
        return await client.Select<T>(new RecordIdOfString(table, recordKey), ct);
    }

    private async Task<T> GetRequiredRecordAsync<T>(string table, string recordKey, string resourceType, string resourceId, CancellationToken ct)
        where T : class
    {
        return await SelectRecordAsync<T>(table, recordKey, ct)
            ?? throw CosmosEmulatorException.NotFound(resourceType, resourceId);
    }

    private async Task CreateRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
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

    private async Task UpsertRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
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

    private async Task DeleteRecordAsync(string table, string recordKey, string resourceType, string resourceId, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var deleted = await client.Delete(new RecordIdOfString(table, recordKey), ct);
        if (!deleted)
        {
            throw CosmosEmulatorException.NotFound(resourceType, resourceId);
        }
    }

    private static DbStoredProcedureRecord ToRecord(StoredProcedure sproc) => new()
    {
        Id = sproc.Id,
        DatabaseId = sproc.DatabaseId,
        ContainerId = sproc.ContainerId,
        Rid = sproc.Rid,
        ETag = sproc.ETag,
        Timestamp = sproc.Timestamp,
        Body = sproc.Body
    };

    private static StoredProcedure ToStoredProcedure(DbStoredProcedureRecord record) => new()
    {
        Id = record.Id,
        DatabaseId = record.DatabaseId,
        ContainerId = record.ContainerId,
        Rid = record.Rid,
        ETag = record.ETag,
        Timestamp = record.Timestamp,
        Self = $"dbs/{record.DatabaseId}/colls/{record.ContainerId}/sprocs/{record.Id}/",
        Body = record.Body
    };

    private static DbTriggerRecord ToRecord(Trigger trigger) => new()
    {
        Id = trigger.Id,
        DatabaseId = trigger.DatabaseId,
        ContainerId = trigger.ContainerId,
        Rid = trigger.Rid,
        ETag = trigger.ETag,
        Timestamp = trigger.Timestamp,
        Body = trigger.Body,
        TriggerType = (int)trigger.TriggerType,
        TriggerOperation = (int)trigger.TriggerOperation
    };

    private static Trigger ToTrigger(DbTriggerRecord record) => new()
    {
        Id = record.Id,
        DatabaseId = record.DatabaseId,
        ContainerId = record.ContainerId,
        Rid = record.Rid,
        ETag = record.ETag,
        Timestamp = record.Timestamp,
        Self = $"dbs/{record.DatabaseId}/colls/{record.ContainerId}/triggers/{record.Id}/",
        Body = record.Body,
        TriggerType = (TriggerType)record.TriggerType,
        TriggerOperation = (TriggerOperation)record.TriggerOperation
    };

    private static DbUserDefinedFunctionRecord ToRecord(UserDefinedFunction udf) => new()
    {
        Id = udf.Id,
        DatabaseId = udf.DatabaseId,
        ContainerId = udf.ContainerId,
        Rid = udf.Rid,
        ETag = udf.ETag,
        Timestamp = udf.Timestamp,
        Body = udf.Body
    };

    private static UserDefinedFunction ToUserDefinedFunction(DbUserDefinedFunctionRecord record) => new()
    {
        Id = record.Id,
        DatabaseId = record.DatabaseId,
        ContainerId = record.ContainerId,
        Rid = record.Rid,
        ETag = record.ETag,
        Timestamp = record.Timestamp,
        Self = $"dbs/{record.DatabaseId}/colls/{record.ContainerId}/udfs/{record.Id}/",
        Body = record.Body
    };

    private static string MakeRecordKey(string dbId, string containerId, string resourceId) =>
        $"{EncodeRecordKey(dbId)}:{EncodeRecordKey(containerId)}:{EncodeRecordKey(resourceId)}";

    private static string EncodeRecordKey(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsJintException(Exception ex) =>
        ex is JavaScriptException or ParseErrorException;

    private static string FormatExecutionError(Exception ex) => ex switch
    {
        JavaScriptException jsEx => jsEx.GetJavaScriptErrorString(),
        ParseErrorException parseError => parseError.Message,
        _ => ex.Message
    };
}
