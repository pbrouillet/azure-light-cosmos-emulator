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

    [JsonPropertyName("maxThroughput")]
    public int? MaxThroughput { get; set; }
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

    [JsonPropertyName("maxThroughput")]
    public int? MaxThroughput { get; set; }

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

internal sealed class DbUserRecord
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
}

internal sealed class DbPermissionRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("databaseId")]
    public string DatabaseId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("eTag")]
    public string ETag { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("permissionMode")]
    public int PermissionMode { get; set; }

    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

internal sealed class DbOfferRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("rid")]
    public string Rid { get; set; } = string.Empty;

    [JsonPropertyName("eTag")]
    public string ETag { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("offerThroughput")]
    public int OfferThroughput { get; set; }

    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("offerResourceId")]
    public string OfferResourceId { get; set; } = string.Empty;
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
    private const string UserTable = "cosmos_users";
    private const string PermissionTable = "cosmos_permissions";
    private const string OfferTable = "cosmos_offers";
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

    public async Task<CosmosDatabase> ReplaceDatabaseAsync(CosmosDatabase database, CancellationToken ct = default)
    {
        var databaseKey = MakeDatabaseRecordKey(database.Id);
        var existing = await GetRequiredRecordAsync<DbDatabaseRecord>(DatabaseTable, databaseKey, "Database", database.Id, ct);

        var updated = new CosmosDatabase
        {
            Id = database.Id,
            Rid = existing.Rid,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MaxThroughput = database.MaxThroughput
        };

        await UpsertRecordAsync(DatabaseTable, databaseKey, ToRecord(updated), ct);
        return updated;
    }

    public async Task DeleteDatabaseAsync(string id, CancellationToken ct = default)
    {
        var databaseKey = MakeDatabaseRecordKey(id);
        await EnsureRecordExistsAsync<DbDatabaseRecord>(DatabaseTable, databaseKey, "Database", id, ct);

        var containers = await SelectTableRecordsAsync<DbContainerRecord>(ContainerTable, ct);
        foreach (var container in containers.Where(container => string.Equals(container.DatabaseId, id, StringComparison.Ordinal)))
        {
            await DeleteOfferForContainerAsync(container.Rid, ct);
            await DeleteRecordAsync(ContainerTable, MakeContainerRecordKey(container.DatabaseId, container.Id), "Container", container.Id, ct);
        }

        var documents = await SelectTableRecordsAsync<DbDocumentRecord>(DocumentTable, ct);
        foreach (var document in documents.Where(document => string.Equals(document.DatabaseId, id, StringComparison.Ordinal)))
        {
            await DeleteRecordAsync(DocumentTable, MakeDocumentRecordKey(document.DatabaseId, document.ContainerId, document.Id, DeserializePartitionKey(document.PartitionKeyJson)), "Document", document.Id, ct);
        }

        // Cascade delete users and their permissions
        var users = (await SelectTableRecordsAsync<DbUserRecord>(UserTable, ct))
            .Where(u => string.Equals(u.DatabaseId, id, StringComparison.Ordinal));
        foreach (var user in users)
        {
            var permissions = (await SelectTableRecordsAsync<DbPermissionRecord>(PermissionTable, ct))
                .Where(p => string.Equals(p.DatabaseId, id, StringComparison.Ordinal) && string.Equals(p.UserId, user.Id, StringComparison.Ordinal));
            foreach (var p in permissions)
                await DeleteRecordAsync(PermissionTable, MakePermissionRecordKey(id, user.Id, p.Id), "Permission", p.Id, ct);
            await DeleteRecordAsync(UserTable, MakeUserRecordKey(id, user.Id), "User", user.Id, ct);
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
        await CreateOfferForContainerAsync(container, ct);
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
            MaxThroughput = container.MaxThroughput,
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
        var containerRecord = await GetRequiredRecordAsync<DbContainerRecord>(ContainerTable, containerKey, "Container", containerId, ct);

        var documents = await SelectTableRecordsAsync<DbDocumentRecord>(DocumentTable, ct);
        foreach (var document in documents.Where(document =>
                     string.Equals(document.DatabaseId, databaseId, StringComparison.Ordinal)
                     && string.Equals(document.ContainerId, containerId, StringComparison.Ordinal)))
        {
            await DeleteRecordAsync(DocumentTable, MakeDocumentRecordKey(document.DatabaseId, document.ContainerId, document.Id, DeserializePartitionKey(document.PartitionKeyJson)), "Document", document.Id, ct);
        }

        await DeleteOfferForContainerAsync(containerRecord.Rid, ct);
        await DeleteRecordAsync(ContainerTable, containerKey, "Container", containerId, ct);
    }

    public async Task<CosmosDocument> CreateDocumentAsync(string databaseId, string containerId, JsonObject document, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(databaseId, containerId, ct);
        var id = document["id"]?.GetValue<string>()
                 ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

        EnforceDocumentSizeLimit(document);

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
            TimeToLive = ExtractTimeToLive(document),
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
        EnforceDocumentSizeLimit(document);

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
            TimeToLive = ExtractTimeToLive(document),
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

    public async Task<CosmosDocument> PatchDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, IReadOnlyList<PatchOperation> operations, string? ifMatch = null, string? condition = null, CancellationToken ct = default)
    {
        var documentKey = MakeDocumentRecordKey(databaseId, containerId, documentId, partitionKey);
        var existingRecord = await GetRequiredRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, "Document", documentId, ct);
        var existing = ToCosmosDocument(existingRecord);

        if (ifMatch is not null && existing.ETag != ifMatch)
        {
            throw CosmosEmulatorException.PreconditionFailed($"ETag mismatch. Expected: {ifMatch}, Actual: {existing.ETag}");
        }

        if (!string.IsNullOrWhiteSpace(condition))
        {
            var condBody = existing.ToResponseBody();
            if (!EvaluatePatchCondition(condBody, condition))
            {
                throw CosmosEmulatorException.PreconditionFailed(
                    $"The patch condition '{condition}' was not satisfied for document '{documentId}'.");
            }
        }

        var body = existing.Body.DeepClone().AsObject();
        ApplyPatchOperations(body, operations);
        EnforceDocumentSizeLimit(body);

        var updated = new CosmosDocument
        {
            Id = documentId,
            Rid = existing.Rid,
            DatabaseId = databaseId,
            ContainerId = containerId,
            PartitionKey = partitionKey,
            Body = body,
            TimeToLive = ExtractTimeToLive(body),
            Lsn = await GetNextLsnAsync(ct),
            Self = existing.Self,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await UpsertRecordAsync(DocumentTable, documentKey, ToRecord(updated), ct);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, updated, ChangeType.Replace, existing, ct);
        return updated;
    }

    public async Task DeleteDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        var documentKey = MakeDocumentRecordKey(databaseId, containerId, documentId, partitionKey);
        var removedRecord = await GetRequiredRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, "Document", documentId, ct);
        var removed = ToCosmosDocument(removedRecord);
        removed.Lsn = await GetNextLsnAsync(ct);
        removed.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await DeleteRecordAsync(DocumentTable, documentKey, "Document", documentId, ct);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, removed, ChangeType.Delete, ct: ct);
    }

    public async Task<long> GetGlobalLsnAsync(CancellationToken ct = default)
    {
        var meta = await SelectRecordAsync<DbMetaRecord>(MetaTable, MakeMetaRecordKey(GlobalLsnKey), ct);
        return meta?.Value ?? await GetLatestLsnAsync(ct);
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

    // ─── Batch operations ───────────────────────────────────────────

    public async Task<IReadOnlyList<BatchOperationResponse>> ExecuteBatchAsync(
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        IReadOnlyList<BatchOperationRequest> operations,
        CancellationToken ct = default)
    {
        var container = await GetContainerAsync(databaseId, containerId, ct);

        // Track snapshots for rollback and which keys were newly created
        var snapshots = new Dictionary<string, DbDocumentRecord?>();
        var createdKeys = new List<string>();
        var results = new List<BatchOperationResponse>();
        var changeFeedEntries = new List<(CosmosDocument doc, ChangeType type, CosmosDocument? previous)>();
        var failedIndex = -1;

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            try
            {
                var result = await ExecuteBatchOperationAsync(
                    databaseId, containerId, partitionKey, container.PartitionKey,
                    op, snapshots, createdKeys, changeFeedEntries, ct);
                results.Add(result);
            }
            catch (CosmosEmulatorException ex)
            {
                failedIndex = i;
                results.Add(new BatchOperationResponse
                {
                    StatusCode = (int)ex.StatusCode,
                    RequestCharge = ex.RequestCharge
                });
                break;
            }
        }

        if (failedIndex >= 0)
        {
            // Rollback all previously successful operations
            await RollbackBatchAsync(snapshots, createdKeys, ct);

            // Mark all previous results as 424 Failed Dependency
            for (var j = 0; j < failedIndex; j++)
            {
                results[j] = new BatchOperationResponse
                {
                    StatusCode = 424,
                    RequestCharge = 0
                };
            }

            // Mark remaining operations as 424 Failed Dependency
            for (var j = failedIndex + 1; j < operations.Count; j++)
            {
                results.Add(new BatchOperationResponse
                {
                    StatusCode = 424,
                    RequestCharge = 0
                });
            }
        }
        else
        {
            // All operations succeeded — record change feed entries
            foreach (var (doc, type, previous) in changeFeedEntries)
            {
                await _changeFeed.RecordChangeAsync(databaseId, containerId, doc, type, previous, ct);
            }
        }

        return results;
    }

    private async Task<BatchOperationResponse> ExecuteBatchOperationAsync(
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        PartitionKeyDefinition pkDef,
        BatchOperationRequest op,
        Dictionary<string, DbDocumentRecord?> snapshots,
        List<string> createdKeys,
        List<(CosmosDocument doc, ChangeType type, CosmosDocument? previous)> changeFeedEntries,
        CancellationToken ct)
    {
        switch (op.OperationType)
        {
            case BatchOperationType.Create:
            {
                var body = op.ResourceBody
                    ?? throw CosmosEmulatorException.BadRequest("Create operation requires a resourceBody.");
                var id = body["id"]?.GetValue<string>()
                    ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

                EnforceDocumentSizeLimit(body);
                var docPk = ExtractPartitionKey(body, pkDef);
                var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, docPk);

                // Snapshot original state for rollback (only first time we touch this key)
                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct);

                // Check current state in storage
                var currentRecord = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct);
                if (currentRecord is not null)
                    throw CosmosEmulatorException.Conflict("Document", id);

                var created = new CosmosDocument
                {
                    Id = id,
                    DatabaseId = databaseId,
                    ContainerId = containerId,
                    PartitionKey = docPk,
                    Body = body.DeepClone().AsObject(),
                    TimeToLive = ExtractTimeToLive(body),
                    Lsn = await GetNextLsnAsync(ct),
                    Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/"
                };

                await CreateRecordAsync(DocumentTable, documentKey, ToRecord(created), ct);
                createdKeys.Add(documentKey);
                changeFeedEntries.Add((created, ChangeType.Create, null));

                var bodySize = body.ToJsonString().Length;
                return new BatchOperationResponse
                {
                    StatusCode = 201,
                    ResourceBody = created.ToResponseBody(),
                    ETag = created.ETag,
                    RequestCharge = RuCostCalculator.Create(bodySize)
                };
            }

            case BatchOperationType.Read:
            {
                var id = op.Id
                    ?? throw CosmosEmulatorException.BadRequest("Read operation requires an id.");
                var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, partitionKey);
                var record = await GetRequiredRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, "Document", id, ct);
                var doc = ToCosmosDocument(record);
                var bodySize = doc.Body.ToJsonString().Length;
                return new BatchOperationResponse
                {
                    StatusCode = 200,
                    ResourceBody = doc.ToResponseBody(),
                    ETag = doc.ETag,
                    RequestCharge = RuCostCalculator.PointRead(bodySize)
                };
            }

            case BatchOperationType.Replace:
            {
                var id = op.Id
                    ?? throw CosmosEmulatorException.BadRequest("Replace operation requires an id.");
                var body = op.ResourceBody
                    ?? throw CosmosEmulatorException.BadRequest("Replace operation requires a resourceBody.");

                EnforceDocumentSizeLimit(body);
                var docPk = ExtractPartitionKey(body, pkDef);
                var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, docPk);

                // Snapshot original state for rollback (only first time we touch this key)
                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct);

                // Read current state from storage
                var existingRecord = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct)
                    ?? throw CosmosEmulatorException.NotFound("Document", id);
                var existing = ToCosmosDocument(existingRecord);

                if (op.IfMatch is not null && existing.ETag != op.IfMatch)
                    throw CosmosEmulatorException.PreconditionFailed(
                        $"ETag mismatch. Expected: {op.IfMatch}, Actual: {existing.ETag}");

                var updated = new CosmosDocument
                {
                    Id = id,
                    Rid = existing.Rid,
                    DatabaseId = databaseId,
                    ContainerId = containerId,
                    PartitionKey = docPk,
                    Body = body.DeepClone().AsObject(),
                    TimeToLive = ExtractTimeToLive(body),
                    Lsn = await GetNextLsnAsync(ct),
                    Self = existing.Self,
                    ETag = ETagGenerator.Generate(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                await UpsertRecordAsync(DocumentTable, documentKey, ToRecord(updated), ct);
                changeFeedEntries.Add((updated, ChangeType.Replace, existing));

                var bodySize = body.ToJsonString().Length;
                return new BatchOperationResponse
                {
                    StatusCode = 200,
                    ResourceBody = updated.ToResponseBody(),
                    ETag = updated.ETag,
                    RequestCharge = RuCostCalculator.Replace(bodySize)
                };
            }

            case BatchOperationType.Upsert:
            {
                var body = op.ResourceBody
                    ?? throw CosmosEmulatorException.BadRequest("Upsert operation requires a resourceBody.");
                var id = body["id"]?.GetValue<string>()
                    ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

                EnforceDocumentSizeLimit(body);
                var docPk = ExtractPartitionKey(body, pkDef);
                var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, docPk);

                // Snapshot original state for rollback (only first time we touch this key)
                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct);

                // Read current state from storage
                var existingRecord = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct);
                CosmosDocument? existing = existingRecord is not null ? ToCosmosDocument(existingRecord) : null;

                if (existing is not null)
                {
                    var updated = new CosmosDocument
                    {
                        Id = id,
                        Rid = existing.Rid,
                        DatabaseId = databaseId,
                        ContainerId = containerId,
                        PartitionKey = docPk,
                        Body = body.DeepClone().AsObject(),
                        TimeToLive = ExtractTimeToLive(body),
                        Lsn = await GetNextLsnAsync(ct),
                        Self = existing.Self,
                        ETag = ETagGenerator.Generate(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    await UpsertRecordAsync(DocumentTable, documentKey, ToRecord(updated), ct);
                    changeFeedEntries.Add((updated, ChangeType.Replace, existing));

                    var bodySize = body.ToJsonString().Length;
                    return new BatchOperationResponse
                    {
                        StatusCode = 200,
                        ResourceBody = updated.ToResponseBody(),
                        ETag = updated.ETag,
                        RequestCharge = RuCostCalculator.Upsert(bodySize)
                    };
                }
                else
                {
                    var created = new CosmosDocument
                    {
                        Id = id,
                        DatabaseId = databaseId,
                        ContainerId = containerId,
                        PartitionKey = docPk,
                        Body = body.DeepClone().AsObject(),
                        TimeToLive = ExtractTimeToLive(body),
                        Lsn = await GetNextLsnAsync(ct),
                        Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/"
                    };

                    await CreateRecordAsync(DocumentTable, documentKey, ToRecord(created), ct);
                    createdKeys.Add(documentKey);
                    changeFeedEntries.Add((created, ChangeType.Create, null));

                    var bodySize = body.ToJsonString().Length;
                    return new BatchOperationResponse
                    {
                        StatusCode = 201,
                        ResourceBody = created.ToResponseBody(),
                        ETag = created.ETag,
                        RequestCharge = RuCostCalculator.Create(bodySize)
                    };
                }
            }

            case BatchOperationType.Delete:
            {
                var id = op.Id
                    ?? throw CosmosEmulatorException.BadRequest("Delete operation requires an id.");
                var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, partitionKey);

                // Snapshot original state for rollback (only first time we touch this key)
                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct);

                // Read current state from storage
                var existingRecord = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct)
                    ?? throw CosmosEmulatorException.NotFound("Document", id);
                var existing = ToCosmosDocument(existingRecord);
                existing.Lsn = await GetNextLsnAsync(ct);
                existing.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                await DeleteRecordAsync(DocumentTable, documentKey, "Document", id, ct);
                changeFeedEntries.Add((existing, ChangeType.Delete, null));

                return new BatchOperationResponse
                {
                    StatusCode = 204,
                    RequestCharge = RuCostCalculator.Delete()
                };
            }

            case BatchOperationType.Patch:
            {
                var id = op.Id
                    ?? throw CosmosEmulatorException.BadRequest("Patch operation requires an id.");
                var patchBody = op.ResourceBody
                    ?? throw CosmosEmulatorException.BadRequest("Patch operation requires a resourceBody with operations.");
                var opsArray = patchBody["operations"] as JsonArray
                    ?? throw CosmosEmulatorException.BadRequest("Patch resourceBody must include an 'operations' array.");

                var documentKey = MakeDocumentRecordKey(databaseId, containerId, id, partitionKey);

                // Snapshot original state for rollback (only first time we touch this key)
                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct);

                // Read current state from storage
                var existingRecord = await SelectRecordAsync<DbDocumentRecord>(DocumentTable, documentKey, ct)
                    ?? throw CosmosEmulatorException.NotFound("Document", id);
                var existing = ToCosmosDocument(existingRecord);

                if (op.IfMatch is not null && existing.ETag != op.IfMatch)
                    throw CosmosEmulatorException.PreconditionFailed(
                        $"ETag mismatch. Expected: {op.IfMatch}, Actual: {existing.ETag}");

                var patchOps = new List<PatchOperation>();
                foreach (var opNode in opsArray)
                {
                    if (opNode is not JsonObject opObj)
                        throw CosmosEmulatorException.BadRequest("Each patch operation must be a JSON object.");
                    var opType = opObj["op"]?.GetValue<string>();
                    var path = opObj["path"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(opType) || string.IsNullOrEmpty(path))
                        throw CosmosEmulatorException.BadRequest("Each patch operation must have 'op' and 'path'.");
                    patchOps.Add(new PatchOperation
                    {
                        Op = opType,
                        Path = path,
                        Value = opObj.ContainsKey("value") ? opObj["value"] : null,
                        From = opObj["from"]?.GetValue<string>()
                    });
                }

                var body = existing.Body.DeepClone().AsObject();
                ApplyPatchOperations(body, patchOps);
                EnforceDocumentSizeLimit(body);

                var updated = new CosmosDocument
                {
                    Id = id,
                    Rid = existing.Rid,
                    DatabaseId = databaseId,
                    ContainerId = containerId,
                    PartitionKey = partitionKey,
                    Body = body,
                    TimeToLive = ExtractTimeToLive(body),
                    Lsn = await GetNextLsnAsync(ct),
                    Self = existing.Self,
                    ETag = ETagGenerator.Generate(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                await UpsertRecordAsync(DocumentTable, documentKey, ToRecord(updated), ct);
                changeFeedEntries.Add((updated, ChangeType.Replace, existing));

                var bodySize = body.ToJsonString().Length;
                return new BatchOperationResponse
                {
                    StatusCode = 200,
                    ResourceBody = updated.ToResponseBody(),
                    ETag = updated.ETag,
                    RequestCharge = RuCostCalculator.Replace(bodySize)
                };
            }

            default:
                throw CosmosEmulatorException.BadRequest($"Unsupported batch operation type: {op.OperationType}.");
        }
    }

    private async Task RollbackBatchAsync(
        Dictionary<string, DbDocumentRecord?> snapshots,
        List<string> createdKeys,
        CancellationToken ct)
    {
        // Delete newly created records
        foreach (var key in createdKeys)
        {
            try
            {
                var client = await GetClientAsync(ct);
                await client.Delete(new RecordIdOfString(DocumentTable, key), ct);
            }
            catch
            {
                // Best effort rollback
            }
        }

        // Restore modified records to their original state
        foreach (var (key, snapshot) in snapshots)
        {
            if (createdKeys.Contains(key))
                continue; // Already handled above

            try
            {
                if (snapshot is not null)
                {
                    // Restore original record
                    await UpsertRecordAsync(DocumentTable, key, snapshot, ct);
                }
                // If snapshot was null and the record was deleted in the batch,
                // we'd need to recreate it — but that case is handled by the fact
                // that if delete succeeded and something later failed, the original
                // snapshot was non-null.
            }
            catch
            {
                // Best effort rollback
            }
        }
    }

    // ─── User operations ────────────────────────────────────────────

    public async Task<CosmosUser> CreateUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        await EnsureRecordExistsAsync<DbDatabaseRecord>(DatabaseTable, MakeDatabaseRecordKey(databaseId), "Database", databaseId, ct);
        var key = MakeUserRecordKey(databaseId, userId);
        if (await SelectRecordAsync<DbUserRecord>(UserTable, key, ct) is not null)
            throw CosmosEmulatorException.Conflict("User", userId);

        var user = new CosmosUser { Id = userId, DatabaseId = databaseId, Self = $"dbs/{databaseId}/users/{userId}/" };
        await CreateRecordAsync(UserTable, key, ToRecord(user), ct);
        return user;
    }

    public async Task<CosmosUser> GetUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbUserRecord>(UserTable, MakeUserRecordKey(databaseId, userId), "User", userId, ct);
        return ToCosmosUser(record);
    }

    public async Task<FeedResponse<CosmosUser>> ListUsersAsync(string databaseId, CancellationToken ct = default)
    {
        await EnsureRecordExistsAsync<DbDatabaseRecord>(DatabaseTable, MakeDatabaseRecordKey(databaseId), "Database", databaseId, ct);
        var records = (await SelectTableRecordsAsync<DbUserRecord>(UserTable, ct))
            .Where(r => string.Equals(r.DatabaseId, databaseId, StringComparison.Ordinal))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        return new FeedResponse<CosmosUser> { Resources = records.Select(ToCosmosUser).ToList() };
    }

    public async Task<CosmosUser> ReplaceUserAsync(string databaseId, CosmosUser user, CancellationToken ct = default)
    {
        var key = MakeUserRecordKey(databaseId, user.Id);
        var existing = await GetRequiredRecordAsync<DbUserRecord>(UserTable, key, "User", user.Id, ct);
        var updated = new CosmosUser
        {
            Id = user.Id, DatabaseId = databaseId, Rid = existing.Rid,
            Self = $"dbs/{databaseId}/users/{user.Id}/",
            ETag = ETagGenerator.Generate(), Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await UpsertRecordAsync(UserTable, key, ToRecord(updated), ct);
        return updated;
    }

    public async Task DeleteUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        var key = MakeUserRecordKey(databaseId, userId);
        await EnsureRecordExistsAsync<DbUserRecord>(UserTable, key, "User", userId, ct);
        var permissions = (await SelectTableRecordsAsync<DbPermissionRecord>(PermissionTable, ct))
            .Where(p => string.Equals(p.DatabaseId, databaseId, StringComparison.Ordinal) && string.Equals(p.UserId, userId, StringComparison.Ordinal));
        foreach (var p in permissions)
            await DeleteRecordAsync(PermissionTable, MakePermissionRecordKey(databaseId, userId, p.Id), "Permission", p.Id, ct);
        await DeleteRecordAsync(UserTable, key, "User", userId, ct);
    }

    // ─── Permission operations ──────────────────────────────────────

    public async Task<CosmosPermission> CreatePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default)
    {
        await EnsureRecordExistsAsync<DbUserRecord>(UserTable, MakeUserRecordKey(databaseId, userId), "User", userId, ct);
        var key = MakePermissionRecordKey(databaseId, userId, permission.Id);
        if (await SelectRecordAsync<DbPermissionRecord>(PermissionTable, key, ct) is not null)
            throw CosmosEmulatorException.Conflict("Permission", permission.Id);

        permission.DatabaseId = databaseId;
        permission.UserId = userId;
        permission.Self = $"dbs/{databaseId}/users/{userId}/permissions/{permission.Id}/";
        permission.Token = GenerateResourceToken(permission);
        await CreateRecordAsync(PermissionTable, key, ToRecord(permission), ct);
        return permission;
    }

    public async Task<CosmosPermission> GetPermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbPermissionRecord>(PermissionTable, MakePermissionRecordKey(databaseId, userId, permissionId), "Permission", permissionId, ct);
        return ToCosmosPermission(record);
    }

    public async Task<FeedResponse<CosmosPermission>> ListPermissionsAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        await EnsureRecordExistsAsync<DbUserRecord>(UserTable, MakeUserRecordKey(databaseId, userId), "User", userId, ct);
        var records = (await SelectTableRecordsAsync<DbPermissionRecord>(PermissionTable, ct))
            .Where(r => string.Equals(r.DatabaseId, databaseId, StringComparison.Ordinal) && string.Equals(r.UserId, userId, StringComparison.Ordinal))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        return new FeedResponse<CosmosPermission> { Resources = records.Select(ToCosmosPermission).ToList() };
    }

    public async Task<CosmosPermission> ReplacePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default)
    {
        var key = MakePermissionRecordKey(databaseId, userId, permission.Id);
        var existing = await GetRequiredRecordAsync<DbPermissionRecord>(PermissionTable, key, "Permission", permission.Id, ct);
        var updated = new CosmosPermission
        {
            Id = permission.Id, DatabaseId = databaseId, UserId = userId,
            Rid = existing.Rid, Self = $"dbs/{databaseId}/users/{userId}/permissions/{permission.Id}/",
            ETag = ETagGenerator.Generate(), Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PermissionMode = permission.PermissionMode, Resource = permission.Resource,
            Token = GenerateResourceToken(permission)
        };
        await UpsertRecordAsync(PermissionTable, key, ToRecord(updated), ct);
        return updated;
    }

    public async Task DeletePermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default)
    {
        await DeleteRecordAsync(PermissionTable, MakePermissionRecordKey(databaseId, userId, permissionId), "Permission", permissionId, ct);
    }

    // ─── Offer operations ───────────────────────────────────────────

    public async Task<CosmosOffer> GetOfferAsync(string offerId, CancellationToken ct = default)
    {
        var record = await GetRequiredRecordAsync<DbOfferRecord>(OfferTable, MakeOfferRecordKey(offerId), "Offer", offerId, ct);
        return ToCosmosOffer(record);
    }

    public async Task<FeedResponse<CosmosOffer>> ListOffersAsync(CancellationToken ct = default)
    {
        var records = (await SelectTableRecordsAsync<DbOfferRecord>(OfferTable, ct))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        return new FeedResponse<CosmosOffer> { Resources = records.Select(ToCosmosOffer).ToList() };
    }

    public async Task<CosmosOffer> ReplaceOfferAsync(CosmosOffer offer, CancellationToken ct = default)
    {
        var key = MakeOfferRecordKey(offer.Id);
        var existing = await GetRequiredRecordAsync<DbOfferRecord>(OfferTable, key, "Offer", offer.Id, ct);
        existing.OfferThroughput = offer.Content.OfferThroughput;
        existing.ETag = ETagGenerator.Generate();
        existing.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await UpsertRecordAsync(OfferTable, key, existing, ct);
        return ToCosmosOffer(existing);
    }

    internal async Task CreateOfferForContainerAsync(CosmosContainer container, CancellationToken ct)
    {
        var offer = new CosmosOffer
        {
            Content = new OfferContent { OfferThroughput = container.MaxThroughput },
            Resource = container.Self,
            OfferResourceId = container.Rid
        };
        offer.Rid = offer.Id;
        await CreateRecordAsync(OfferTable, MakeOfferRecordKey(offer.Id), ToRecord(offer), ct);
    }

    internal async Task DeleteOfferForContainerAsync(string containerRid, CancellationToken ct)
    {
        var all = await SelectTableRecordsAsync<DbOfferRecord>(OfferTable, ct);
        foreach (var o in all.Where(o => string.Equals(o.OfferResourceId, containerRid, StringComparison.Ordinal)))
            await DeleteRecordAsync(OfferTable, MakeOfferRecordKey(o.Id), "Offer", o.Id, ct);
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
        Timestamp = database.Timestamp,
        MaxThroughput = database.MaxThroughput
    };

    private static CosmosDatabase ToCosmosDatabase(DbDatabaseRecord record) => new()
    {
        Id = record.Id,
        Rid = record.Rid,
        ETag = record.ETag,
        Timestamp = record.Timestamp,
        MaxThroughput = record.MaxThroughput
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
        MaxThroughput = container.MaxThroughput,
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
        MaxThroughput = record.MaxThroughput ?? 400,
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

    private static int? ExtractTimeToLive(JsonObject document)
    {
        if (document["ttl"] is null)
        {
            return null;
        }

        return document["ttl"]?.GetValue<int>();
    }

    private const int MaxDocumentSizeBytes = 2 * 1024 * 1024; // 2 MB

    private static void EnforceDocumentSizeLimit(JsonObject document)
    {
        var size = document.ToJsonString().Length;
        if (size > MaxDocumentSizeBytes)
        {
            throw CosmosEmulatorException.EntityTooLarge(
                $"The document size ({size} bytes) exceeds the maximum allowed size ({MaxDocumentSizeBytes} bytes).");
        }
    }

    private static void ApplyPatchOperations(JsonObject document, IReadOnlyList<PatchOperation> operations)
    {
        foreach (var op in operations)
        {
            var segments = op.Path.TrimStart('/').Split('/');
            switch (op.Op.ToLowerInvariant())
            {
                case "add":
                case "set":
                    SetNestedValue(document, segments, ConvertPatchValue(op.Value));
                    break;
                case "replace":
                    if (!TryGetParentAndKey(document, segments, out var replaceParent, out var replaceKey))
                        throw CosmosEmulatorException.BadRequest($"Path '{op.Path}' does not exist for replace operation.");
                    if (replaceParent is JsonObject replaceObj && !replaceObj.ContainsKey(replaceKey))
                        throw CosmosEmulatorException.BadRequest($"Path '{op.Path}' does not exist for replace operation.");
                    SetNestedValue(document, segments, ConvertPatchValue(op.Value));
                    break;
                case "remove":
                    if (TryGetParentAndKey(document, segments, out var removeParent, out var removeKey))
                    {
                        if (removeParent is JsonObject removeObj)
                            removeObj.Remove(removeKey);
                        else if (removeParent is JsonArray removeArr && int.TryParse(removeKey, out var idx))
                            removeArr.RemoveAt(idx);
                    }
                    break;
                case "incr":
                    IncrementValue(document, segments, op.Value);
                    break;
                case "move":
                    if (string.IsNullOrEmpty(op.From))
                        throw CosmosEmulatorException.BadRequest("Move operation requires 'from' property.");
                    var fromSegments = op.From.TrimStart('/').Split('/');
                    var value = GetNestedValue(document, fromSegments);
                    if (TryGetParentAndKey(document, fromSegments, out var moveFromParent, out var moveFromKey)
                        && moveFromParent is JsonObject moveFromObj)
                        moveFromObj.Remove(moveFromKey);
                    SetNestedValue(document, segments, value?.DeepClone());
                    break;
                default:
                    throw CosmosEmulatorException.BadRequest($"Unknown patch operation: '{op.Op}'.");
            }
        }
    }

    private static void SetNestedValue(JsonObject root, string[] segments, JsonNode? value)
    {
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current is JsonObject obj)
            {
                if (!obj.ContainsKey(segments[i]))
                    obj[segments[i]] = new JsonObject();
                current = obj[segments[i]]!;
            }
            else if (current is JsonArray arr && int.TryParse(segments[i], out var idx))
            {
                current = arr[idx]!;
            }
        }

        var lastKey = segments[^1];
        if (current is JsonObject parentObj)
            parentObj[lastKey] = value;
        else if (current is JsonArray parentArr && int.TryParse(lastKey, out var arrIdx))
            parentArr[arrIdx] = value;
    }

    private static JsonNode? GetNestedValue(JsonObject root, string[] segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (current is JsonObject obj)
                current = obj[segment];
            else if (current is JsonArray arr && int.TryParse(segment, out var idx))
                current = arr[idx];
            else
                return null;
        }
        return current;
    }

    private static bool TryGetParentAndKey(JsonObject root, string[] segments, out JsonNode? parent, out string key)
    {
        parent = root;
        key = segments[^1];
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (parent is JsonObject obj)
                parent = obj[segments[i]];
            else if (parent is JsonArray arr && int.TryParse(segments[i], out var idx))
                parent = arr[idx];
            else
                return false;
        }
        return parent is not null;
    }

    private static void IncrementValue(JsonObject root, string[] segments, object? incrementBy)
    {
        var current = GetNestedValue(root, segments);
        if (current is null)
        {
            SetNestedValue(root, segments, ConvertPatchValue(incrementBy));
            return;
        }

        var currentVal = current.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? current.GetValue<double>()
            : throw CosmosEmulatorException.BadRequest($"Cannot increment non-numeric value at '{string.Join("/", segments)}'.");

        var incrVal = incrementBy switch
        {
            int i => (double)i,
            long l => (double)l,
            double d => d,
            float f => (double)f,
            System.Text.Json.Nodes.JsonNode node when node.GetValueKind() == System.Text.Json.JsonValueKind.Number => node.GetValue<double>(),
            _ => throw CosmosEmulatorException.BadRequest("Increment value must be a number.")
        };

        SetNestedValue(root, segments, JsonValue.Create(currentVal + incrVal));
    }

    private static JsonNode? ConvertPatchValue(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        _ => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value))
    };

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

    // ─── User record helpers ────────────────────────────────────────

    private static string MakeUserRecordKey(string dbId, string userId) =>
        $"{EncodeRecordKey(dbId)}:{EncodeRecordKey(userId)}";

    private static DbUserRecord ToRecord(CosmosUser user) => new()
    {
        Id = user.Id, DatabaseId = user.DatabaseId, Rid = user.Rid, ETag = user.ETag, Timestamp = user.Timestamp
    };

    private static CosmosUser ToCosmosUser(DbUserRecord r) => new()
    {
        Id = r.Id, DatabaseId = r.DatabaseId, Rid = r.Rid, ETag = r.ETag, Timestamp = r.Timestamp,
        Self = $"dbs/{r.DatabaseId}/users/{r.Id}/"
    };

    // ─── Permission record helpers ──────────────────────────────────

    private static string MakePermissionRecordKey(string dbId, string userId, string permissionId) =>
        $"{EncodeRecordKey(dbId)}:{EncodeRecordKey(userId)}:{EncodeRecordKey(permissionId)}";

    private static DbPermissionRecord ToRecord(CosmosPermission p) => new()
    {
        Id = p.Id, DatabaseId = p.DatabaseId, UserId = p.UserId, Rid = p.Rid, ETag = p.ETag,
        Timestamp = p.Timestamp, PermissionMode = (int)p.PermissionMode, Resource = p.Resource, Token = p.Token
    };

    private static CosmosPermission ToCosmosPermission(DbPermissionRecord r) => new()
    {
        Id = r.Id, DatabaseId = r.DatabaseId, UserId = r.UserId, Rid = r.Rid, ETag = r.ETag,
        Timestamp = r.Timestamp, Self = $"dbs/{r.DatabaseId}/users/{r.UserId}/permissions/{r.Id}/",
        PermissionMode = (PermissionMode)r.PermissionMode, Resource = r.Resource, Token = r.Token
    };

    private static string GenerateResourceToken(CosmosPermission permission)
    {
        var payload = $"{permission.Resource}:{permission.PermissionMode}:{permission.Id}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(permission.Rid));
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return $"type=resource&ver=1.0&sig={sig};";
    }

    private static bool EvaluatePatchCondition(JsonObject document, string condition)
    {
        // Condition format: "from c where c.Field = 'value'"
        var trimmed = condition.Trim();
        if (trimmed.StartsWith("from", StringComparison.OrdinalIgnoreCase))
        {
            var whereIdx = trimmed.IndexOf("where", StringComparison.OrdinalIgnoreCase);
            if (whereIdx < 0) return true;
            trimmed = trimmed[(whereIdx + "where".Length)..].Trim();
        }

        // Simple equality check: c.Path = 'value' or c.Path = number
        var match = System.Text.RegularExpressions.Regex.Match(trimmed,
            @"^(\w+)\.(\w+(?:\.\w+)*)\s*(=|!=|>|<|>=|<=)\s*(?:'([^']*)'|(\d+(?:\.\d+)?))$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return true; // Can't parse → allow

        var propPath = match.Groups[2].Value;
        var op = match.Groups[3].Value;
        var strVal = match.Groups[4].Success ? match.Groups[4].Value : null;
        var numVal = match.Groups[5].Success ? double.Parse(match.Groups[5].Value) : (double?)null;

        var segments = propPath.Split('.');
        JsonNode? current = document;
        foreach (var seg in segments)
        {
            if (current is JsonObject obj) current = obj[seg];
            else return false;
        }

        if (current is null) return op == "!=";

        if (strVal is not null)
        {
            var actualStr = current.GetValueKind() == JsonValueKind.String ? current.GetValue<string>() : current.ToJsonString();
            return op switch
            {
                "=" => string.Equals(actualStr, strVal, StringComparison.Ordinal),
                "!=" => !string.Equals(actualStr, strVal, StringComparison.Ordinal),
                _ => false
            };
        }

        if (numVal is not null && current.GetValueKind() == JsonValueKind.Number)
        {
            var actualNum = current.GetValue<double>();
            return op switch
            {
                "=" => Math.Abs(actualNum - numVal.Value) < 0.0001,
                "!=" => Math.Abs(actualNum - numVal.Value) >= 0.0001,
                ">" => actualNum > numVal.Value,
                "<" => actualNum < numVal.Value,
                ">=" => actualNum >= numVal.Value,
                "<=" => actualNum <= numVal.Value,
                _ => false
            };
        }

        return false;
    }

    // ─── Offer record helpers ───────────────────────────────────────

    private static string MakeOfferRecordKey(string offerId) => EncodeRecordKey(offerId);

    private static DbOfferRecord ToRecord(CosmosOffer o) => new()
    {
        Id = o.Id, Rid = o.Rid, ETag = o.ETag, Timestamp = o.Timestamp,
        OfferThroughput = o.Content.OfferThroughput, Resource = o.Resource, OfferResourceId = o.OfferResourceId
    };

    private static CosmosOffer ToCosmosOffer(DbOfferRecord r) => new()
    {
        Id = r.Id, Rid = r.Rid, ETag = r.ETag, Timestamp = r.Timestamp,
        Content = new OfferContent { OfferThroughput = r.OfferThroughput },
        Resource = r.Resource, OfferResourceId = r.OfferResourceId
    };
}
