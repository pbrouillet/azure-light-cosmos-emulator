using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.Storage.SurrealDb;

internal sealed class DbDatabaseRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("eTag")]
    public string ETag { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

internal sealed class DbContainerRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("databaseId")]
    public string DatabaseId { get; set; } = string.Empty;

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("eTag")]
    public string ETag { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("partitionKeyJson")]
    public string PartitionKeyJson { get; set; } = string.Empty;

    [JsonPropertyName("indexingPolicyJson")]
    public string? IndexingPolicyJson { get; set; }

    [JsonPropertyName("defaultTimeToLive")]
    public int? DefaultTimeToLive { get; set; }

    [JsonPropertyName("uniqueKeyPolicyJson")]
    public string? UniqueKeyPolicyJson { get; set; }

    [JsonPropertyName("conflictResolutionPolicyJson")]
    public string? ConflictResolutionPolicyJson { get; set; }
}

internal sealed class DbDocumentRecord
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

    [JsonPropertyName("partitionKeyJson")]
    public string PartitionKeyJson { get; set; } = string.Empty;

    [JsonPropertyName("bodyJson")]
    public string BodyJson { get; set; } = string.Empty;

    [JsonPropertyName("lsn")]
    public long Lsn { get; set; }

    [JsonPropertyName("timeToLive")]
    public int? TimeToLive { get; set; }
}

internal sealed class DbMetaRecord
{
    [JsonPropertyName("value")]
    public long Value { get; set; }
}

/// <summary>
/// SurrealDB-backed implementation of IDocumentStore.
/// Uses SurrealDB embedded with RocksDB KV backend.
/// </summary>
public class SurrealDbDocumentStore : IDocumentStore
{
    private const string DatabaseTable = "cosmos_databases";
    private const string ContainerTable = "cosmos_containers";
    private const string DocumentTable = "cosmos_documents";
    private const string MetaTable = "cosmos_meta";
    private const string GlobalLsnKey = "global_lsn";

    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();

    private readonly SurrealDbConnectionManager _connectionManager;
    private readonly IChangeFeedProvider _changeFeed;
    private readonly SemaphoreSlim _lsnLock = new(1, 1);

    public SurrealDbDocumentStore(SurrealDbConnectionManager connectionManager, IChangeFeedProvider changeFeed)
    {
        _connectionManager = connectionManager;
        _changeFeed = changeFeed;
    }

    public async Task<CosmosDatabase> CreateDatabaseAsync(string id, CancellationToken ct = default)
    {
        var databaseKey = MakeDatabaseRecordKey(id);
        if (await SelectRecordAsync<DbDatabaseRecord>(DatabaseTable, databaseKey, ct) is not null)
        {
            throw CosmosEmulatorException.Conflict("Database", id);
        }

        var database = new CosmosDatabase { Id = id };
        await CreateRecordAsync(DatabaseTable, databaseKey, ToRecord(database), ct);
        return database;
    }

    public async Task<CosmosDatabase> GetDatabaseAsync(string id, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbDatabaseRecord>(DatabaseTable, MakeDatabaseRecordKey(id), "Database", id, ct);
        return ToCosmosDatabase(record);
    }

    public async Task<FeedResponse<CosmosDatabase>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var records = await QueryRecordsAsync<DbDatabaseRecord>(
            $"SELECT * FROM {DatabaseTable} ORDER BY id ASC",
            EmptyParameters,
            ct);

        return new FeedResponse<CosmosDatabase>
        {
            Resources = records.Select(ToCosmosDatabase).ToList()
        };
    }

    public async Task DeleteDatabaseAsync(string id, CancellationToken ct = default)
    {
        var databaseKey = MakeDatabaseRecordKey(id);
        await EnsureRecordExistsAsync<DbDatabaseRecord>(DatabaseTable, databaseKey, "Database", id, ct);

        var containers = await SelectTableRecordsAsync<DbContainerRecord>(ContainerTable, ct);
        foreach (var container in containers.Where(container => string.Equals(container.DatabaseId, id, StringComparison.Ordinal)))
        {
            await DeleteRecordAsync(ContainerTable, MakeContainerRecordKey(container.DatabaseId, container.Id), "Container", container.Id, ct);
        }

        var documents = await SelectTableRecordsAsync<DbDocumentRecord>(DocumentTable, ct);
        foreach (var document in documents.Where(document => string.Equals(document.DatabaseId, id, StringComparison.Ordinal)))
        {
            await DeleteRecordAsync(DocumentTable, MakeDocumentRecordKey(document.DatabaseId, document.ContainerId, document.Id, DeserializePartitionKey(document.PartitionKeyJson)), "Document", document.Id, ct);
        }

        await DeleteRecordAsync(DatabaseTable, databaseKey, "Database", id, ct);
    }

    public async Task<CosmosContainer> CreateContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default)
    {
        await EnsureRecordExistsAsync<DbDatabaseRecord>(DatabaseTable, MakeDatabaseRecordKey(databaseId), "Database", databaseId, ct);

        var containerKey = MakeContainerRecordKey(databaseId, container.Id);
        if (await SelectRecordAsync<DbContainerRecord>(ContainerTable, containerKey, ct) is not null)
        {
            throw CosmosEmulatorException.Conflict("Container", container.Id);
        }

        container.DatabaseId = databaseId;
        container.Self = $"dbs/{databaseId}/colls/{container.Id}/";

        await CreateRecordAsync(ContainerTable, containerKey, ToRecord(container), ct);
        return container;
    }

    public async Task<CosmosContainer> GetContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbContainerRecord>(
            ContainerTable,
            MakeContainerRecordKey(databaseId, containerId),
            "Container",
            containerId,
            ct);

        return ToCosmosContainer(record);
    }

    public async Task<FeedResponse<CosmosContainer>> ListContainersAsync(string databaseId, CancellationToken ct = default)
    {
        await EnsureRecordExistsAsync<DbDatabaseRecord>(DatabaseTable, MakeDatabaseRecordKey(databaseId), "Database", databaseId, ct);

        var records = (await SelectTableRecordsAsync<DbContainerRecord>(ContainerTable, ct))
            .Where(record => string.Equals(record.DatabaseId, databaseId, StringComparison.Ordinal))
            .OrderBy(record => record.Id, StringComparer.Ordinal)
            .ToList();

        return new FeedResponse<CosmosContainer>
        {
            Resources = records.Select(ToCosmosContainer).ToList()
        };
    }

    public async Task<CosmosContainer> ReplaceContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default)
    {
        var containerKey = MakeContainerRecordKey(databaseId, container.Id);
        var existing = await GetRequiredRecordAsync<DbContainerRecord>(ContainerTable, containerKey, "Container", container.Id, ct);

        var updated = new CosmosContainer
        {
            Id = container.Id,
            DatabaseId = databaseId,
            PartitionKey = container.PartitionKey,
            IndexingPolicy = container.IndexingPolicy,
            DefaultTimeToLive = container.DefaultTimeToLive,
            UniqueKeyPolicy = container.UniqueKeyPolicy,
            ConflictResolutionPolicy = container.ConflictResolutionPolicy,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/colls/{container.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await UpsertRecordAsync(ContainerTable, containerKey, ToRecord(updated), ct);
        return updated;
    }

    public async Task DeleteContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var containerKey = MakeContainerRecordKey(databaseId, containerId);
        await EnsureRecordExistsAsync<DbContainerRecord>(ContainerTable, containerKey, "Container", containerId, ct);

        var documents = await SelectTableRecordsAsync<DbDocumentRecord>(DocumentTable, ct);
        foreach (var document in documents.Where(document =>
                     string.Equals(document.DatabaseId, databaseId, StringComparison.Ordinal)
                     && string.Equals(document.ContainerId, containerId, StringComparison.Ordinal)))
        {
            await DeleteRecordAsync(DocumentTable, MakeDocumentRecordKey(document.DatabaseId, document.ContainerId, document.Id, DeserializePartitionKey(document.PartitionKeyJson)), "Document", document.Id, ct);
        }

        await DeleteRecordAsync(ContainerTable, containerKey, "Container", containerId, ct);
    }

    public async Task<CosmosDocument> CreateDocumentAsync(string databaseId, string containerId, JsonObject document, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(databaseId, containerId, ct);
        var id = document["id"]?.GetValue<string>()
                 ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, partitionKey);
        if (await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct) is not null)
        {
            throw CosmosEmulatorException.Conflict("Document", id);
        }

        var created = new CosmosDocument
        {
            Id = id,
            DatabaseId = databaseId,
            ContainerId = containerId,
            PartitionKey = partitionKey,
            Body = document.DeepClone().AsObject(),
            Lsn = await GetNextLsnAsync(ct),
            Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/"
        };

        await CreateRecordAsync(DocumentTable, documentKey, ToRecord(created), ct);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, created, ChangeType.Create, ct: ct);
        return created;
    }

    public async Task<CosmosDocument> ReadDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbDocumentRecord>(
            DocumentTable,
            MakeDocumentRecordKey(databaseId, containerId, documentId, partitionKey),
            "Document",
            documentId,
            ct);

        return ToCosmosDocument(record);
    }

    public async Task<CosmosDocument> ReplaceDocumentAsync(string databaseId, string containerId, string documentId, JsonObject document, string? ifMatch = null, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(databaseId, containerId, ct);
        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var documentKey = MakeDocumentRecordKey(databaseId, containerId, documentId, partitionKey);
        var existingRecord = await GetRequiredRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, "Document", documentId, ct);
        var existing = ToCosmosDocument(existingRecord);

        if (ifMatch is not null && existing.ETag != ifMatch)
        {
            throw CosmosEmulatorException.PreconditionFailed($"ETag mismatch. Expected: {ifMatch}, Actual: {existing.ETag}");
        }

        var updated = new CosmosDocument
        {
            Id = documentId,
            Rid = existing.Rid,
            DatabaseId = databaseId,
            ContainerId = containerId,
            PartitionKey = partitionKey,
            Body = document.DeepClone().AsObject(),
            TimeToLive = existing.TimeToLive,
            Lsn = await GetNextLsnAsync(ct),
            Self = existing.Self,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await UpsertRecordAsync(DocumentTable, documentKey, ToRecord(updated), ct);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, updated, ChangeType.Replace, existing, ct);
        return updated;
    }

    public async Task<CosmosDocument> UpsertDocumentAsync(string databaseId, string containerId, JsonObject document, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(databaseId, containerId, ct);
        var id = document["id"]?.GetValue<string>()
                 ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, partitionKey);

        if (await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct) is not null)
        {
            return await ReplaceDocumentAsync(databaseId, containerId, id, document, ct: ct);
        }

        return await CreateDocumentAsync(databaseId, containerId, document, ct);
    }

    public async Task DeleteDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        var documentKey = MakeDocumentRecordKey(databaseId, containerId, documentId, partitionKey);
        var removedRecord = await GetRequiredRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, "Document", documentId, ct);
        var removed = ToCosmosDocument(removedRecord);

        await DeleteRecordAsync(DocumentTable, documentKey, "Document", documentId, ct);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, removed, ChangeType.Delete, ct: ct);
    }

    public async Task<FeedResponse<CosmosDocument>> ReadManyDocumentsAsync(string databaseId, string containerId, IEnumerable<(string id, PartitionKeyValue pk)> items, CancellationToken ct = default)
    {
        var documents = new List<CosmosDocument>();
        foreach (var (id, partitionKey) in items)
        {
            var record = await SelectRecordAsync<DbDocumentRecord>(
                DocumentTable,
                MakeDocumentRecordKey(databaseId, containerId, id, partitionKey),
                ct);

            if (record is not null)
            {
                documents.Add(ToCosmosDocument(record));
            }
        }

        return new FeedResponse<CosmosDocument> { Resources = documents };
    }

    public async Task<FeedResponse<CosmosDocument>> ListDocumentsAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var records = (await SelectTableRecordsAsync<DbDocumentRecord>(DocumentTable, ct))
            .Where(record => string.Equals(record.DatabaseId, databaseId, StringComparison.Ordinal)
                && string.Equals(record.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(record => record.Timestamp)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .ToList();

        return new FeedResponse<CosmosDocument>
        {
            Resources = records.Select(ToCosmosDocument).ToList()
        };
    }

    private async Task<long> GetNextLsnAsync(CancellationToken ct)
    {
        await _lsnLock.WaitAsync(ct);
        try
        {
            var metaKey = MakeMetaRecordKey(GlobalLsnKey);
            var meta = await SelectRecordAsync<DbMetaRecord>(MetaTable, metaKey, ct);
            var current = meta?.Value ?? await GetLatestLsnAsync(ct);
            var next = current + 1;

            await UpsertRecordAsync(MetaTable, metaKey, new DbMetaRecord { Value = next }, ct);
            return next;
        }
        finally
        {
            _lsnLock.Release();
        }
    }

    private async Task<long> GetLatestLsnAsync(CancellationToken ct)
    {
        var latest = await SelectTableRecordsAsync<DbDocumentRecord>(DocumentTable, ct);
        return latest.OrderByDescending(record => record.Lsn).FirstOrDefault()?.Lsn ?? 0;
    }

    private async Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var response = await client.RawQuery(sql, parameters ?? EmptyParameters, ct);
        response.EnsureAllOks();
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

    private async Task EnsureRecordExistsAsync<T>(string table, string recordKey, string resourceType, string resourceId, CancellationToken ct)
        where T : class
    {
        _ = await GetRequiredRecordAsync<T>(table, recordKey, resourceType, resourceId, ct);
    }

    private async Task CreateRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
    {
        await ExecuteAsync(
            "CREATE $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(table, recordKey),
                ["data"] = record
            },
            ct);
    }

    private async Task UpsertRecordAsync<T>(string table, string recordKey, T record, CancellationToken ct)
    {
        await ExecuteAsync(
            "UPSERT $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(table, recordKey),
                ["data"] = record
            },
            ct);
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

    private static DbDatabaseRecord ToRecord(CosmosDatabase database) => new()
    {
        Id = database.Id,
        Rid = database.Rid,
        ETag = database.ETag,
        Timestamp = database.Timestamp
    };

    private static CosmosDatabase ToCosmosDatabase(DbDatabaseRecord record) => new()
    {
        Id = record.Id,
        Rid = record.Rid,
        ETag = record.ETag,
        Timestamp = record.Timestamp
    };

    private static DbContainerRecord ToRecord(CosmosContainer container) => new()
    {
        Id = container.Id,
        DatabaseId = container.DatabaseId,
        Rid = container.Rid,
        ETag = container.ETag,
        Timestamp = container.Timestamp,
        PartitionKeyJson = JsonSerializer.Serialize(container.PartitionKey, JsonOptions),
        IndexingPolicyJson = JsonSerializer.Serialize(container.IndexingPolicy, JsonOptions),
        DefaultTimeToLive = container.DefaultTimeToLive,
        UniqueKeyPolicyJson = SerializeNullable(container.UniqueKeyPolicy),
        ConflictResolutionPolicyJson = SerializeNullable(container.ConflictResolutionPolicy)
    };

    private static CosmosContainer ToCosmosContainer(DbContainerRecord record) => new()
    {
        Id = record.Id,
        DatabaseId = record.DatabaseId,
        Rid = record.Rid,
        ETag = record.ETag,
        Timestamp = record.Timestamp,
        Self = $"dbs/{record.DatabaseId}/colls/{record.Id}/",
        PartitionKey = DeserializeRequired<PartitionKeyDefinition>(record.PartitionKeyJson),
        IndexingPolicy = string.IsNullOrWhiteSpace(record.IndexingPolicyJson)
            ? new IndexingPolicy()
            : DeserializeRequired<IndexingPolicy>(record.IndexingPolicyJson),
        DefaultTimeToLive = record.DefaultTimeToLive,
        UniqueKeyPolicy = DeserializeNullable<UniqueKeyPolicy>(record.UniqueKeyPolicyJson),
        ConflictResolutionPolicy = DeserializeNullable<ConflictResolutionPolicy>(record.ConflictResolutionPolicyJson)
    };

    private static DbDocumentRecord ToRecord(CosmosDocument document) => new()
    {
        Id = document.Id,
        DatabaseId = document.DatabaseId,
        ContainerId = document.ContainerId,
        Rid = document.Rid,
        ETag = document.ETag,
        Timestamp = document.Timestamp,
        PartitionKeyJson = SerializePartitionKey(document.PartitionKey),
        BodyJson = document.Body.ToJsonString(),
        Lsn = document.Lsn,
        TimeToLive = document.TimeToLive
    };

    private static CosmosDocument ToCosmosDocument(DbDocumentRecord record) => new()
    {
        Id = record.Id,
        DatabaseId = record.DatabaseId,
        ContainerId = record.ContainerId,
        Rid = record.Rid,
        ETag = record.ETag,
        Timestamp = record.Timestamp,
        Self = $"dbs/{record.DatabaseId}/colls/{record.ContainerId}/docs/{record.Id}/",
        PartitionKey = DeserializePartitionKey(record.PartitionKeyJson),
        Body = DeserializeJsonObject(record.BodyJson),
        Lsn = record.Lsn,
        TimeToLive = record.TimeToLive
    };

    private static string? SerializeNullable<T>(T? value)
        where T : class => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private static T DeserializeRequired<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Unable to deserialize {typeof(T).Name}.");

    private static T? DeserializeNullable<T>(string? json)
        where T : class => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static JsonObject DeserializeJsonObject(string json) =>
        JsonNode.Parse(json)?.AsObject()
        ?? throw new InvalidOperationException("Unable to deserialize persisted document body.");

    private static string SerializePartitionKey(PartitionKeyValue partitionKey) =>
        JsonSerializer.Serialize(partitionKey.Components, JsonOptions);

    private static PartitionKeyValue DeserializePartitionKey(string json)
    {
        var values = JsonNode.Parse(json)?.AsArray()
            ?.Select(ConvertJsonNodeToValue)
            .ToList()
            ?? throw new InvalidOperationException("Unable to deserialize persisted partition key.");

        return new PartitionKeyValue { Components = values };
    }

    private static string MakeDatabaseRecordKey(string databaseId) => EncodeRecordKey(databaseId);

    private static string MakeContainerRecordKey(string databaseId, string containerId) =>
        $"{EncodeRecordKey(databaseId)}:{EncodeRecordKey(containerId)}";

    private static string MakeDocumentRecordKey(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey) =>
        $"{EncodeRecordKey(databaseId)}:{EncodeRecordKey(containerId)}:{EncodeRecordKey(partitionKey.ToHeaderString())}:{EncodeRecordKey(documentId)}";

    private static string MakeMetaRecordKey(string key) => EncodeRecordKey(key);

    private static string EncodeRecordKey(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static PartitionKeyValue ExtractPartitionKey(JsonObject document, PartitionKeyDefinition pkDef)
    {
        var values = new List<object?>();

        foreach (var path in pkDef.Paths)
        {
            var propertyName = path.TrimStart('/');
            values.Add(ConvertJsonNodeToValue(document[propertyName]));
        }

        return new PartitionKeyValue { Components = values };
    }

    private static object? ConvertJsonNodeToValue(JsonNode? node) => node switch
    {
        null => null,
        _ when node.GetValueKind() == JsonValueKind.Null => null,
        _ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
        _ when node.GetValueKind() == JsonValueKind.Number => node.GetValue<double>(),
        _ when node.GetValueKind() == JsonValueKind.True => true,
        _ when node.GetValueKind() == JsonValueKind.False => false,
        _ => node.ToJsonString()
    };
}
