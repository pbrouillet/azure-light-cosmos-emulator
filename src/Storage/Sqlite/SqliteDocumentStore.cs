using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.Data.Sqlite;
using static Azure.Cosmos.LightEmulator.Storage.DocumentStoreHelpers;

namespace Azure.Cosmos.LightEmulator.Storage.Sqlite;

/// <summary>
/// SQLite-backed implementation of IDocumentStore.
/// </summary>
public class SqliteDocumentStore : IDocumentStore
{
    private const string GlobalLsnKey = "global_lsn";

    private readonly SqliteConnectionManager _connectionManager;
    private readonly IChangeFeedProvider _changeFeed;
    private readonly SemaphoreSlim _lsnLock = new(1, 1);

    public SqliteDocumentStore(SqliteConnectionManager connectionManager, IChangeFeedProvider changeFeed)
    {
        _connectionManager = connectionManager;
        _changeFeed = changeFeed;
    }

    // ─── Database operations ────────────────────────────────────────

    public Task<CosmosDatabase> CreateDatabaseAsync(string id, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();

        if (DatabaseExists(connection, id))
            throw CosmosEmulatorException.Conflict("Database", id);

        var database = new CosmosDatabase { Id = id };
        InsertDatabase(connection, database);
        return Task.FromResult(database);
    }

    public Task<CosmosDatabase> GetDatabaseAsync(string id, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var db = ReadDatabase(connection, id)
            ?? throw CosmosEmulatorException.NotFound("Database", id);
        return Task.FromResult(db);
    }

    public Task<FeedResponse<CosmosDatabase>> ListDatabasesAsync(CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, rid, etag, timestamp, max_throughput FROM databases ORDER BY id ASC";

        var databases = new List<CosmosDatabase>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            databases.Add(ReadDatabaseFromReader(reader));

        return Task.FromResult(new FeedResponse<CosmosDatabase> { Resources = databases });
    }

    public Task<CosmosDatabase> ReplaceDatabaseAsync(CosmosDatabase database, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var existing = ReadDatabase(connection, database.Id)
            ?? throw CosmosEmulatorException.NotFound("Database", database.Id);

        var updated = new CosmosDatabase
        {
            Id = database.Id,
            Rid = existing.Rid,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MaxThroughput = database.MaxThroughput
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE databases SET rid = @rid, etag = @etag, timestamp = @timestamp, max_throughput = @maxThroughput WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", updated.Id);
        cmd.Parameters.AddWithValue("@rid", updated.Rid);
        cmd.Parameters.AddWithValue("@etag", updated.ETag);
        cmd.Parameters.AddWithValue("@timestamp", updated.Timestamp);
        cmd.Parameters.AddWithValue("@maxThroughput", (object?)updated.MaxThroughput ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return Task.FromResult(updated);
    }

    public Task DeleteDatabaseAsync(string id, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        if (!DatabaseExists(connection, id))
            throw CosmosEmulatorException.NotFound("Database", id);

        using var transaction = connection.BeginTransaction();

        // Delete all documents for this database
        ExecuteNonQuery(connection, "DELETE FROM documents WHERE database_id = @id", ("@id", id));
        // Delete offers for containers in this database
        ExecuteNonQuery(connection, "DELETE FROM offers WHERE offer_resource_id IN (SELECT rid FROM containers WHERE database_id = @id)", ("@id", id));
        // Delete all containers
        ExecuteNonQuery(connection, "DELETE FROM containers WHERE database_id = @id", ("@id", id));
        // Delete permissions
        ExecuteNonQuery(connection, "DELETE FROM permissions WHERE database_id = @id", ("@id", id));
        // Delete users
        ExecuteNonQuery(connection, "DELETE FROM users WHERE database_id = @id", ("@id", id));
        // Delete the database itself
        ExecuteNonQuery(connection, "DELETE FROM databases WHERE id = @id", ("@id", id));

        transaction.Commit();
        return Task.CompletedTask;
    }

    // ─── Container operations ───────────────────────────────────────

    public Task<CosmosContainer> CreateContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();

        if (!DatabaseExists(connection, databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        if (ContainerExists(connection, databaseId, container.Id))
            throw CosmosEmulatorException.Conflict("Container", container.Id);

        container.DatabaseId = databaseId;
        container.Self = $"dbs/{databaseId}/colls/{container.Id}/";

        InsertContainer(connection, container);
        CreateOfferForContainer(connection, container);
        return Task.FromResult(container);
    }

    public Task<CosmosContainer> GetContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var container = ReadContainer(connection, databaseId, containerId)
            ?? throw CosmosEmulatorException.NotFound("Container", containerId);
        return Task.FromResult(container);
    }

    public Task<FeedResponse<CosmosContainer>> ListContainersAsync(string databaseId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();

        if (!DatabaseExists(connection, databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, rid, etag, timestamp, partition_key_json, indexing_policy_json, default_ttl, max_throughput, unique_key_policy_json, conflict_resolution_policy_json, vector_embedding_policy_json FROM containers WHERE database_id = @databaseId ORDER BY id ASC";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);

        var containers = new List<CosmosContainer>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            containers.Add(ReadContainerFromReader(reader));

        return Task.FromResult(new FeedResponse<CosmosContainer> { Resources = containers });
    }

    public Task<CosmosContainer> ReplaceContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var existing = ReadContainer(connection, databaseId, container.Id)
            ?? throw CosmosEmulatorException.NotFound("Container", container.Id);

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

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE containers SET
                rid = @rid, etag = @etag, timestamp = @timestamp,
                partition_key_json = @partitionKeyJson, indexing_policy_json = @indexingPolicyJson,
                default_ttl = @defaultTtl, max_throughput = @maxThroughput,
                unique_key_policy_json = @uniqueKeyPolicyJson,
                conflict_resolution_policy_json = @conflictResolutionPolicyJson,
                vector_embedding_policy_json = @vectorEmbeddingPolicyJson
            WHERE database_id = @databaseId AND id = @id
        """;
        AddContainerParameters(cmd, updated);
        cmd.ExecuteNonQuery();

        return Task.FromResult(updated);
    }

    public Task DeleteContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var container = ReadContainer(connection, databaseId, containerId)
            ?? throw CosmosEmulatorException.NotFound("Container", containerId);

        using var transaction = connection.BeginTransaction();
        ExecuteNonQuery(connection, "DELETE FROM documents WHERE database_id = @dbId AND container_id = @collId",
            ("@dbId", databaseId), ("@collId", containerId));
        ExecuteNonQuery(connection, "DELETE FROM offers WHERE offer_resource_id = @rid", ("@rid", container.Rid));
        ExecuteNonQuery(connection, "DELETE FROM containers WHERE database_id = @dbId AND id = @collId",
            ("@dbId", databaseId), ("@collId", containerId));
        transaction.Commit();

        return Task.CompletedTask;
    }

    // ─── Document operations ────────────────────────────────────────

    public async Task<CosmosDocument> CreateDocumentAsync(string databaseId, string containerId, JsonObject document, bool? isIndexed = null, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var container = ReadContainer(connection, databaseId, containerId)
            ?? throw CosmosEmulatorException.NotFound("Container", containerId);

        var id = document["id"]?.GetValue<string>()
            ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

        EnforceDocumentSizeLimit(document);

        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var pkJson = SerializePartitionKey(partitionKey);

        if (DocumentExists(connection, databaseId, containerId, pkJson, id))
            throw CosmosEmulatorException.Conflict("Document", id);

        EnforceUniqueKeyPolicyForContainer(connection, container, databaseId, containerId, partitionKey, document, null);

        var created = new CosmosDocument
        {
            Id = id,
            DatabaseId = databaseId,
            ContainerId = containerId,
            PartitionKey = partitionKey,
            Body = document.DeepClone().AsObject(),
            TimeToLive = ExtractTimeToLive(document),
            Lsn = GetNextLsn(connection),
            Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/",
            IsIndexed = isIndexed ?? true
        };

        InsertDocument(connection, created, pkJson);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, created, ChangeType.Create, ct: ct);
        return created;
    }

    public Task<CosmosDocument> ReadDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var pkJson = SerializePartitionKey(partitionKey);
        var doc = ReadDocument(connection, databaseId, containerId, pkJson, documentId)
            ?? throw CosmosEmulatorException.NotFound("Document", documentId);
        return Task.FromResult(doc);
    }

    public async Task<CosmosDocument> ReplaceDocumentAsync(string databaseId, string containerId, string documentId, JsonObject document, string? ifMatch = null, bool? isIndexed = null, CancellationToken ct = default)
    {
        EnforceDocumentSizeLimit(document);

        using var connection = _connectionManager.CreateConnection();
        var container = ReadContainer(connection, databaseId, containerId)
            ?? throw CosmosEmulatorException.NotFound("Container", containerId);

        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var pkJson = SerializePartitionKey(partitionKey);
        var existing = ReadDocument(connection, databaseId, containerId, pkJson, documentId)
            ?? throw CosmosEmulatorException.NotFound("Document", documentId);

        if (ifMatch is not null && existing.ETag != ifMatch)
            throw CosmosEmulatorException.PreconditionFailed($"ETag mismatch. Expected: {ifMatch}, Actual: {existing.ETag}");

        EnforceUniqueKeyPolicyForContainer(connection, container, databaseId, containerId, partitionKey, document, documentId);

        var updated = new CosmosDocument
        {
            Id = documentId,
            Rid = existing.Rid,
            DatabaseId = databaseId,
            ContainerId = containerId,
            PartitionKey = partitionKey,
            Body = document.DeepClone().AsObject(),
            TimeToLive = ExtractTimeToLive(document),
            Lsn = GetNextLsn(connection),
            Self = existing.Self,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            IsIndexed = isIndexed ?? existing.IsIndexed
        };

        UpsertDocument(connection, updated, pkJson);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, updated, ChangeType.Replace, existing, ct);
        return updated;
    }

    public async Task<CosmosDocument> UpsertDocumentAsync(string databaseId, string containerId, JsonObject document, bool? isIndexed = null, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var container = ReadContainer(connection, databaseId, containerId)
            ?? throw CosmosEmulatorException.NotFound("Container", containerId);

        var id = document["id"]?.GetValue<string>()
            ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var pkJson = SerializePartitionKey(partitionKey);

        if (DocumentExists(connection, databaseId, containerId, pkJson, id))
            return await ReplaceDocumentAsync(databaseId, containerId, id, document, isIndexed: isIndexed, ct: ct);

        return await CreateDocumentAsync(databaseId, containerId, document, isIndexed, ct);
    }

    public async Task<CosmosDocument> PatchDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, IReadOnlyList<PatchOperation> operations, string? ifMatch = null, string? condition = null, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var pkJson = SerializePartitionKey(partitionKey);
        var existing = ReadDocument(connection, databaseId, containerId, pkJson, documentId)
            ?? throw CosmosEmulatorException.NotFound("Document", documentId);

        if (ifMatch is not null && existing.ETag != ifMatch)
            throw CosmosEmulatorException.PreconditionFailed($"ETag mismatch. Expected: {ifMatch}, Actual: {existing.ETag}");

        if (!string.IsNullOrWhiteSpace(condition))
        {
            var condBody = existing.ToResponseBody();
            if (!EvaluatePatchCondition(condBody, condition))
                throw CosmosEmulatorException.PreconditionFailed(
                    $"The patch condition '{condition}' was not satisfied for document '{documentId}'.");
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
            Lsn = GetNextLsn(connection),
            Self = existing.Self,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        UpsertDocument(connection, updated, pkJson);
        await _changeFeed.RecordChangeAsync(databaseId, containerId, updated, ChangeType.Replace, existing, ct);
        return updated;
    }

    public async Task DeleteDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var pkJson = SerializePartitionKey(partitionKey);
        var removed = ReadDocument(connection, databaseId, containerId, pkJson, documentId)
            ?? throw CosmosEmulatorException.NotFound("Document", documentId);

        removed.Lsn = GetNextLsn(connection);
        removed.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE database_id = @dbId AND container_id = @collId AND partition_key_json = @pkJson AND id = @id";
        cmd.Parameters.AddWithValue("@dbId", databaseId);
        cmd.Parameters.AddWithValue("@collId", containerId);
        cmd.Parameters.AddWithValue("@pkJson", pkJson);
        cmd.Parameters.AddWithValue("@id", documentId);
        cmd.ExecuteNonQuery();

        await _changeFeed.RecordChangeAsync(databaseId, containerId, removed, ChangeType.Delete, ct: ct);
    }

    public async Task<int> EmptyContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        _ = ReadContainer(connection, databaseId, containerId)
            ?? throw CosmosEmulatorException.NotFound("Container", containerId);

        var docs = ListDocumentsFromConnection(connection, databaseId, containerId);
        foreach (var doc in docs)
        {
            doc.Lsn = GetNextLsn(connection);
            doc.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _changeFeed.RecordChangeAsync(databaseId, containerId, doc, ChangeType.Delete, ct: ct);
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE database_id = @dbId AND container_id = @collId";
        cmd.Parameters.AddWithValue("@dbId", databaseId);
        cmd.Parameters.AddWithValue("@collId", containerId);
        cmd.ExecuteNonQuery();

        return docs.Count;
    }

    public Task<long> GetGlobalLsnAsync(CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var lsn = ReadMetaLsn(connection);
        if (lsn.HasValue)
            return Task.FromResult(lsn.Value);

        return Task.FromResult(GetLatestDocumentLsn(connection));
    }

    // ─── Bulk operations ────────────────────────────────────────────

    public Task<FeedResponse<CosmosDocument>> ReadManyDocumentsAsync(string databaseId, string containerId, IEnumerable<(string id, PartitionKeyValue pk)> items, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var documents = new List<CosmosDocument>();
        foreach (var (id, pk) in items)
        {
            var pkJson = SerializePartitionKey(pk);
            var doc = ReadDocument(connection, databaseId, containerId, pkJson, id);
            if (doc is not null)
                documents.Add(doc);
        }

        return Task.FromResult(new FeedResponse<CosmosDocument> { Resources = documents });
    }

    public Task<FeedResponse<CosmosDocument>> ListDocumentsAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var docs = ListDocumentsFromConnection(connection, databaseId, containerId);
        return Task.FromResult(new FeedResponse<CosmosDocument> { Resources = docs });
    }

    // ─── Batch operations ───────────────────────────────────────────

    public async Task<IReadOnlyList<BatchOperationResponse>> ExecuteBatchAsync(
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        IReadOnlyList<BatchOperationRequest> operations,
        CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var container = ReadContainer(connection, databaseId, containerId)
            ?? throw CosmosEmulatorException.NotFound("Container", containerId);

        using var transaction = connection.BeginTransaction();
        var results = new List<BatchOperationResponse>();
        var changeFeedEntries = new List<(CosmosDocument doc, ChangeType type, CosmosDocument? previous)>();
        var failedIndex = -1;

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            try
            {
                var result = await ExecuteBatchOperationAsync(
                    connection, databaseId, containerId, partitionKey, container, op, changeFeedEntries, ct);
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
            transaction.Rollback();

            for (var j = 0; j < failedIndex; j++)
            {
                results[j] = new BatchOperationResponse
                {
                    StatusCode = 424,
                    RequestCharge = 0
                };
            }

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
            transaction.Commit();
            foreach (var (doc, type, previous) in changeFeedEntries)
                await _changeFeed.RecordChangeAsync(databaseId, containerId, doc, type, previous, ct);
        }

        return results;
    }

    private async Task<BatchOperationResponse> ExecuteBatchOperationAsync(
        SqliteConnection connection,
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        CosmosContainer container,
        BatchOperationRequest op,
        List<(CosmosDocument doc, ChangeType type, CosmosDocument? previous)> changeFeedEntries,
        CancellationToken ct)
    {
        var pkDef = container.PartitionKey;
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
                var pkJson = SerializePartitionKey(docPk);

                if (DocumentExists(connection, databaseId, containerId, pkJson, id))
                    throw CosmosEmulatorException.Conflict("Document", id);

                var created = new CosmosDocument
                {
                    Id = id,
                    DatabaseId = databaseId,
                    ContainerId = containerId,
                    PartitionKey = docPk,
                    Body = body.DeepClone().AsObject(),
                    TimeToLive = ExtractTimeToLive(body),
                    Lsn = GetNextLsn(connection),
                    Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/"
                };

                InsertDocument(connection, created, pkJson);
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
                var pkJson = SerializePartitionKey(partitionKey);
                var doc = ReadDocument(connection, databaseId, containerId, pkJson, id)
                    ?? throw CosmosEmulatorException.NotFound("Document", id);
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
                var pkJson = SerializePartitionKey(docPk);
                var existing = ReadDocument(connection, databaseId, containerId, pkJson, id)
                    ?? throw CosmosEmulatorException.NotFound("Document", id);

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
                    Lsn = GetNextLsn(connection),
                    Self = existing.Self,
                    ETag = ETagGenerator.Generate(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                UpsertDocument(connection, updated, pkJson);
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
                var pkJson = SerializePartitionKey(docPk);

                EnforceUniqueKeyPolicyForContainer(connection, container, databaseId, containerId, docPk, body,
                    DocumentExists(connection, databaseId, containerId, pkJson, id) ? id : null);

                var existingDoc = ReadDocument(connection, databaseId, containerId, pkJson, id);

                if (existingDoc is not null)
                {
                    var updated = new CosmosDocument
                    {
                        Id = id,
                        Rid = existingDoc.Rid,
                        DatabaseId = databaseId,
                        ContainerId = containerId,
                        PartitionKey = docPk,
                        Body = body.DeepClone().AsObject(),
                        TimeToLive = ExtractTimeToLive(body),
                        Lsn = GetNextLsn(connection),
                        Self = existingDoc.Self,
                        ETag = ETagGenerator.Generate(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    UpsertDocument(connection, updated, pkJson);
                    changeFeedEntries.Add((updated, ChangeType.Replace, existingDoc));

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
                        Lsn = GetNextLsn(connection),
                        Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/"
                    };

                    InsertDocument(connection, created, pkJson);
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
                var pkJson = SerializePartitionKey(partitionKey);
                var existing = ReadDocument(connection, databaseId, containerId, pkJson, id)
                    ?? throw CosmosEmulatorException.NotFound("Document", id);

                existing.Lsn = GetNextLsn(connection);
                existing.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                using var delCmd = connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM documents WHERE database_id = @dbId AND container_id = @collId AND partition_key_json = @pkJson AND id = @id";
                delCmd.Parameters.AddWithValue("@dbId", databaseId);
                delCmd.Parameters.AddWithValue("@collId", containerId);
                delCmd.Parameters.AddWithValue("@pkJson", pkJson);
                delCmd.Parameters.AddWithValue("@id", id);
                delCmd.ExecuteNonQuery();

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

                var pkJson = SerializePartitionKey(partitionKey);
                var existing = ReadDocument(connection, databaseId, containerId, pkJson, id)
                    ?? throw CosmosEmulatorException.NotFound("Document", id);

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
                    Lsn = GetNextLsn(connection),
                    Self = existing.Self,
                    ETag = ETagGenerator.Generate(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                UpsertDocument(connection, updated, pkJson);
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

    // ─── User operations ────────────────────────────────────────────

    public Task<CosmosUser> CreateUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        if (!DatabaseExists(connection, databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        if (UserExists(connection, databaseId, userId))
            throw CosmosEmulatorException.Conflict("User", userId);

        var user = new CosmosUser { Id = userId, DatabaseId = databaseId, Self = $"dbs/{databaseId}/users/{userId}/" };
        InsertUser(connection, user);
        return Task.FromResult(user);
    }

    public Task<CosmosUser> GetUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var user = ReadUser(connection, databaseId, userId)
            ?? throw CosmosEmulatorException.NotFound("User", userId);
        return Task.FromResult(user);
    }

    public Task<FeedResponse<CosmosUser>> ListUsersAsync(string databaseId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        if (!DatabaseExists(connection, databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, rid, etag, timestamp FROM users WHERE database_id = @databaseId ORDER BY id ASC";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);

        var users = new List<CosmosUser>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            users.Add(ReadUserFromReader(reader));

        return Task.FromResult(new FeedResponse<CosmosUser> { Resources = users });
    }

    public Task<CosmosUser> ReplaceUserAsync(string databaseId, CosmosUser user, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var existing = ReadUser(connection, databaseId, user.Id)
            ?? throw CosmosEmulatorException.NotFound("User", user.Id);

        var updated = new CosmosUser
        {
            Id = user.Id,
            DatabaseId = databaseId,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/users/{user.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE users SET rid = @rid, etag = @etag, timestamp = @timestamp WHERE database_id = @databaseId AND id = @id";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@id", updated.Id);
        cmd.Parameters.AddWithValue("@rid", updated.Rid);
        cmd.Parameters.AddWithValue("@etag", updated.ETag);
        cmd.Parameters.AddWithValue("@timestamp", updated.Timestamp);
        cmd.ExecuteNonQuery();

        return Task.FromResult(updated);
    }

    public Task DeleteUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        if (!UserExists(connection, databaseId, userId))
            throw CosmosEmulatorException.NotFound("User", userId);

        using var transaction = connection.BeginTransaction();
        ExecuteNonQuery(connection, "DELETE FROM permissions WHERE database_id = @dbId AND user_id = @userId",
            ("@dbId", databaseId), ("@userId", userId));
        ExecuteNonQuery(connection, "DELETE FROM users WHERE database_id = @dbId AND id = @userId",
            ("@dbId", databaseId), ("@userId", userId));
        transaction.Commit();

        return Task.CompletedTask;
    }

    // ─── Permission operations ──────────────────────────────────────

    public Task<CosmosPermission> CreatePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        if (!UserExists(connection, databaseId, userId))
            throw CosmosEmulatorException.NotFound("User", userId);

        if (PermissionExists(connection, databaseId, userId, permission.Id))
            throw CosmosEmulatorException.Conflict("Permission", permission.Id);

        permission.DatabaseId = databaseId;
        permission.UserId = userId;
        permission.Self = $"dbs/{databaseId}/users/{userId}/permissions/{permission.Id}/";
        permission.Token = GenerateResourceToken(permission);

        InsertPermission(connection, permission);
        return Task.FromResult(permission);
    }

    public Task<CosmosPermission> GetPermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var permission = ReadPermission(connection, databaseId, userId, permissionId)
            ?? throw CosmosEmulatorException.NotFound("Permission", permissionId);
        return Task.FromResult(permission);
    }

    public Task<FeedResponse<CosmosPermission>> ListPermissionsAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        if (!UserExists(connection, databaseId, userId))
            throw CosmosEmulatorException.NotFound("User", userId);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, user_id, rid, etag, timestamp, permission_mode, resource, token FROM permissions WHERE database_id = @databaseId AND user_id = @userId ORDER BY id ASC";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@userId", userId);

        var permissions = new List<CosmosPermission>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            permissions.Add(ReadPermissionFromReader(reader));

        return Task.FromResult(new FeedResponse<CosmosPermission> { Resources = permissions });
    }

    public Task<CosmosPermission> ReplacePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var existing = ReadPermission(connection, databaseId, userId, permission.Id)
            ?? throw CosmosEmulatorException.NotFound("Permission", permission.Id);

        var updated = new CosmosPermission
        {
            Id = permission.Id,
            DatabaseId = databaseId,
            UserId = userId,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/users/{userId}/permissions/{permission.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PermissionMode = permission.PermissionMode,
            Resource = permission.Resource,
            Token = GenerateResourceToken(permission)
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE permissions SET
                rid = @rid, etag = @etag, timestamp = @timestamp,
                permission_mode = @permissionMode, resource = @resource, token = @token
            WHERE database_id = @databaseId AND user_id = @userId AND id = @id
        """;
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@id", updated.Id);
        cmd.Parameters.AddWithValue("@rid", updated.Rid);
        cmd.Parameters.AddWithValue("@etag", updated.ETag);
        cmd.Parameters.AddWithValue("@timestamp", updated.Timestamp);
        cmd.Parameters.AddWithValue("@permissionMode", (int)updated.PermissionMode);
        cmd.Parameters.AddWithValue("@resource", updated.Resource);
        cmd.Parameters.AddWithValue("@token", (object?)updated.Token ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return Task.FromResult(updated);
    }

    public Task DeletePermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        if (!PermissionExists(connection, databaseId, userId, permissionId))
            throw CosmosEmulatorException.NotFound("Permission", permissionId);

        ExecuteNonQuery(connection, "DELETE FROM permissions WHERE database_id = @dbId AND user_id = @userId AND id = @pId",
            ("@dbId", databaseId), ("@userId", userId), ("@pId", permissionId));

        return Task.CompletedTask;
    }

    // ─── Offer operations ───────────────────────────────────────────

    public Task<CosmosOffer> GetOfferAsync(string offerId, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var offer = ReadOffer(connection, offerId)
            ?? throw CosmosEmulatorException.NotFound("Offer", offerId);
        return Task.FromResult(offer);
    }

    public Task<FeedResponse<CosmosOffer>> ListOffersAsync(CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, rid, etag, timestamp, offer_throughput, resource, offer_resource_id FROM offers ORDER BY id ASC";

        var offers = new List<CosmosOffer>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            offers.Add(ReadOfferFromReader(reader));

        return Task.FromResult(new FeedResponse<CosmosOffer> { Resources = offers });
    }

    public Task<CosmosOffer> ReplaceOfferAsync(CosmosOffer offer, CancellationToken ct = default)
    {
        using var connection = _connectionManager.CreateConnection();
        var existing = ReadOffer(connection, offer.Id)
            ?? throw CosmosEmulatorException.NotFound("Offer", offer.Id);

        var updated = new CosmosOffer
        {
            Id = existing.Id,
            Rid = existing.Rid,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Content = new OfferContent { OfferThroughput = offer.Content.OfferThroughput },
            Resource = existing.Resource,
            OfferResourceId = existing.OfferResourceId
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE offers SET etag = @etag, timestamp = @timestamp, offer_throughput = @offerThroughput WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", updated.Id);
        cmd.Parameters.AddWithValue("@etag", updated.ETag);
        cmd.Parameters.AddWithValue("@timestamp", updated.Timestamp);
        cmd.Parameters.AddWithValue("@offerThroughput", updated.Content.OfferThroughput);
        cmd.ExecuteNonQuery();

        return Task.FromResult(updated);
    }

    // ─── LSN management ─────────────────────────────────────────────

    /// <summary>
    /// Allocates the next global LSN using an <em>existing</em> connection so the
    /// write participates in that connection's active transaction. This avoids the
    /// "database is locked" error that occurs when a batch holds an open write
    /// transaction and a second connection tries to write the meta LSN row.
    /// </summary>
    private long GetNextLsn(SqliteConnection connection)
    {
        _lsnLock.Wait();
        try
        {
            return AllocateNextLsn(connection);
        }
        finally
        {
            _lsnLock.Release();
        }
    }

    private static long AllocateNextLsn(SqliteConnection connection)
    {
        var current = ReadMetaLsn(connection) ?? GetLatestDocumentLsn(connection);
        var next = current + 1;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES (@key, @value)";
        cmd.Parameters.AddWithValue("@key", GlobalLsnKey);
        cmd.Parameters.AddWithValue("@value", next);
        cmd.ExecuteNonQuery();

        return next;
    }

    private static long? ReadMetaLsn(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", GlobalLsnKey);
        var result = cmd.ExecuteScalar();
        return result is long v ? v : null;
    }

    private static long GetLatestDocumentLsn(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(lsn), 0) FROM documents";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    // ─── Database helpers ───────────────────────────────────────────

    private static bool DatabaseExists(SqliteConnection connection, string id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM databases WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static CosmosDatabase? ReadDatabase(SqliteConnection connection, string id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, rid, etag, timestamp, max_throughput FROM databases WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDatabaseFromReader(reader) : null;
    }

    private static void InsertDatabase(SqliteConnection connection, CosmosDatabase db)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO databases (id, rid, etag, timestamp, max_throughput) VALUES (@id, @rid, @etag, @timestamp, @maxThroughput)";
        cmd.Parameters.AddWithValue("@id", db.Id);
        cmd.Parameters.AddWithValue("@rid", db.Rid);
        cmd.Parameters.AddWithValue("@etag", db.ETag);
        cmd.Parameters.AddWithValue("@timestamp", db.Timestamp);
        cmd.Parameters.AddWithValue("@maxThroughput", (object?)db.MaxThroughput ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static CosmosDatabase ReadDatabaseFromReader(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Rid = reader.GetString(1),
        ETag = reader.GetString(2),
        Timestamp = reader.GetInt64(3),
        MaxThroughput = reader.IsDBNull(4) ? null : reader.GetInt32(4)
    };

    // ─── Container helpers ──────────────────────────────────────────

    private static bool ContainerExists(SqliteConnection connection, string databaseId, string containerId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM containers WHERE database_id = @databaseId AND id = @id";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@id", containerId);
        return cmd.ExecuteScalar() is not null;
    }

    private static CosmosContainer? ReadContainer(SqliteConnection connection, string databaseId, string containerId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, rid, etag, timestamp, partition_key_json, indexing_policy_json, default_ttl, max_throughput, unique_key_policy_json, conflict_resolution_policy_json, vector_embedding_policy_json FROM containers WHERE database_id = @databaseId AND id = @id";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@id", containerId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadContainerFromReader(reader) : null;
    }

    private static void InsertContainer(SqliteConnection connection, CosmosContainer container)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO containers (id, database_id, rid, etag, timestamp, partition_key_json, indexing_policy_json, default_ttl, max_throughput, unique_key_policy_json, conflict_resolution_policy_json, vector_embedding_policy_json)
            VALUES (@id, @databaseId, @rid, @etag, @timestamp, @partitionKeyJson, @indexingPolicyJson, @defaultTtl, @maxThroughput, @uniqueKeyPolicyJson, @conflictResolutionPolicyJson, @vectorEmbeddingPolicyJson)
        """;
        AddContainerParameters(cmd, container);
        cmd.ExecuteNonQuery();
    }

    private static void AddContainerParameters(SqliteCommand cmd, CosmosContainer container)
    {
        cmd.Parameters.AddWithValue("@id", container.Id);
        cmd.Parameters.AddWithValue("@databaseId", container.DatabaseId);
        cmd.Parameters.AddWithValue("@rid", container.Rid);
        cmd.Parameters.AddWithValue("@etag", container.ETag);
        cmd.Parameters.AddWithValue("@timestamp", container.Timestamp);
        cmd.Parameters.AddWithValue("@partitionKeyJson", JsonSerializer.Serialize(container.PartitionKey));
        cmd.Parameters.AddWithValue("@indexingPolicyJson", (object?)JsonSerializer.Serialize(container.IndexingPolicy) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@defaultTtl", (object?)container.DefaultTimeToLive ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@maxThroughput", (object?)container.MaxThroughput ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uniqueKeyPolicyJson", (object?)SerializeNullable(container.UniqueKeyPolicy) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conflictResolutionPolicyJson", (object?)SerializeNullable(container.ConflictResolutionPolicy) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vectorEmbeddingPolicyJson", (object?)SerializeNullable(container.VectorEmbeddingPolicy) ?? DBNull.Value);
    }

    private static CosmosContainer ReadContainerFromReader(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var databaseId = reader.GetString(1);
        var partitionKeyJson = reader.GetString(5);
        var indexingPolicyJson = reader.IsDBNull(6) ? null : reader.GetString(6);

        return new CosmosContainer
        {
            Id = id,
            DatabaseId = databaseId,
            Rid = reader.GetString(2),
            ETag = reader.GetString(3),
            Timestamp = reader.GetInt64(4),
            Self = $"dbs/{databaseId}/colls/{id}/",
            PartitionKey = DeserializeRequired<PartitionKeyDefinition>(partitionKeyJson),
            IndexingPolicy = string.IsNullOrWhiteSpace(indexingPolicyJson)
                ? new IndexingPolicy()
                : DeserializeRequired<IndexingPolicy>(indexingPolicyJson),
            DefaultTimeToLive = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            MaxThroughput = reader.IsDBNull(8) ? 400 : reader.GetInt32(8),
            UniqueKeyPolicy = DeserializeNullable<UniqueKeyPolicy>(reader.IsDBNull(9) ? null : reader.GetString(9)),
            ConflictResolutionPolicy = DeserializeNullable<ConflictResolutionPolicy>(reader.IsDBNull(10) ? null : reader.GetString(10)),
            VectorEmbeddingPolicy = DeserializeNullable<VectorEmbeddingPolicy>(reader.IsDBNull(11) ? null : reader.GetString(11))
        };
    }

    // ─── Document helpers ───────────────────────────────────────────

    private static bool DocumentExists(SqliteConnection connection, string databaseId, string containerId, string pkJson, string documentId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM documents WHERE database_id = @dbId AND container_id = @collId AND partition_key_json = @pkJson AND id = @id";
        cmd.Parameters.AddWithValue("@dbId", databaseId);
        cmd.Parameters.AddWithValue("@collId", containerId);
        cmd.Parameters.AddWithValue("@pkJson", pkJson);
        cmd.Parameters.AddWithValue("@id", documentId);
        return cmd.ExecuteScalar() is not null;
    }

    private static CosmosDocument? ReadDocument(SqliteConnection connection, string databaseId, string containerId, string pkJson, string documentId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, container_id, rid, etag, timestamp, partition_key_json, body_json, lsn, ttl, is_indexed FROM documents WHERE database_id = @dbId AND container_id = @collId AND partition_key_json = @pkJson AND id = @id";
        cmd.Parameters.AddWithValue("@dbId", databaseId);
        cmd.Parameters.AddWithValue("@collId", containerId);
        cmd.Parameters.AddWithValue("@pkJson", pkJson);
        cmd.Parameters.AddWithValue("@id", documentId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDocumentFromReader(reader) : null;
    }

    private static void InsertDocument(SqliteConnection connection, CosmosDocument doc, string pkJson)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (id, database_id, container_id, rid, etag, timestamp, partition_key_json, body_json, lsn, ttl, is_indexed)
            VALUES (@id, @dbId, @collId, @rid, @etag, @timestamp, @pkJson, @bodyJson, @lsn, @ttl, @isIndexed)
        """;
        AddDocumentParameters(cmd, doc, pkJson);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertDocument(SqliteConnection connection, CosmosDocument doc, string pkJson)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO documents (id, database_id, container_id, rid, etag, timestamp, partition_key_json, body_json, lsn, ttl, is_indexed)
            VALUES (@id, @dbId, @collId, @rid, @etag, @timestamp, @pkJson, @bodyJson, @lsn, @ttl, @isIndexed)
        """;
        AddDocumentParameters(cmd, doc, pkJson);
        cmd.ExecuteNonQuery();
    }

    private static void AddDocumentParameters(SqliteCommand cmd, CosmosDocument doc, string pkJson)
    {
        cmd.Parameters.AddWithValue("@id", doc.Id);
        cmd.Parameters.AddWithValue("@dbId", doc.DatabaseId);
        cmd.Parameters.AddWithValue("@collId", doc.ContainerId);
        cmd.Parameters.AddWithValue("@rid", doc.Rid);
        cmd.Parameters.AddWithValue("@etag", doc.ETag);
        cmd.Parameters.AddWithValue("@timestamp", doc.Timestamp);
        cmd.Parameters.AddWithValue("@pkJson", pkJson);
        cmd.Parameters.AddWithValue("@bodyJson", doc.Body.ToJsonString());
        cmd.Parameters.AddWithValue("@lsn", doc.Lsn);
        cmd.Parameters.AddWithValue("@ttl", (object?)doc.TimeToLive ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isIndexed", doc.IsIndexed ? 1 : 0);
    }

    private static CosmosDocument ReadDocumentFromReader(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var databaseId = reader.GetString(1);
        var containerId = reader.GetString(2);

        return new CosmosDocument
        {
            Id = id,
            DatabaseId = databaseId,
            ContainerId = containerId,
            Rid = reader.GetString(3),
            ETag = reader.GetString(4),
            Timestamp = reader.GetInt64(5),
            Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/",
            PartitionKey = DeserializePartitionKey(reader.GetString(6)),
            Body = DeserializeJsonObject(reader.GetString(7)),
            Lsn = reader.GetInt64(8),
            TimeToLive = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            IsIndexed = reader.GetInt32(10) == 1
        };
    }

    private static List<CosmosDocument> ListDocumentsFromConnection(SqliteConnection connection, string databaseId, string containerId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, container_id, rid, etag, timestamp, partition_key_json, body_json, lsn, ttl, is_indexed FROM documents WHERE database_id = @dbId AND container_id = @collId ORDER BY timestamp ASC, id ASC";
        cmd.Parameters.AddWithValue("@dbId", databaseId);
        cmd.Parameters.AddWithValue("@collId", containerId);

        var docs = new List<CosmosDocument>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            docs.Add(ReadDocumentFromReader(reader));
        return docs;
    }

    // ─── User helpers ───────────────────────────────────────────────

    private static bool UserExists(SqliteConnection connection, string databaseId, string userId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM users WHERE database_id = @databaseId AND id = @id";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@id", userId);
        return cmd.ExecuteScalar() is not null;
    }

    private static CosmosUser? ReadUser(SqliteConnection connection, string databaseId, string userId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, rid, etag, timestamp FROM users WHERE database_id = @databaseId AND id = @id";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@id", userId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUserFromReader(reader) : null;
    }

    private static void InsertUser(SqliteConnection connection, CosmosUser user)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO users (id, database_id, rid, etag, timestamp) VALUES (@id, @databaseId, @rid, @etag, @timestamp)";
        cmd.Parameters.AddWithValue("@id", user.Id);
        cmd.Parameters.AddWithValue("@databaseId", user.DatabaseId);
        cmd.Parameters.AddWithValue("@rid", user.Rid);
        cmd.Parameters.AddWithValue("@etag", user.ETag);
        cmd.Parameters.AddWithValue("@timestamp", user.Timestamp);
        cmd.ExecuteNonQuery();
    }

    private static CosmosUser ReadUserFromReader(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var databaseId = reader.GetString(1);
        return new CosmosUser
        {
            Id = id,
            DatabaseId = databaseId,
            Rid = reader.GetString(2),
            ETag = reader.GetString(3),
            Timestamp = reader.GetInt64(4),
            Self = $"dbs/{databaseId}/users/{id}/"
        };
    }

    // ─── Permission helpers ─────────────────────────────────────────

    private static bool PermissionExists(SqliteConnection connection, string databaseId, string userId, string permissionId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM permissions WHERE database_id = @databaseId AND user_id = @userId AND id = @id";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@id", permissionId);
        return cmd.ExecuteScalar() is not null;
    }

    private static CosmosPermission? ReadPermission(SqliteConnection connection, string databaseId, string userId, string permissionId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, database_id, user_id, rid, etag, timestamp, permission_mode, resource, token FROM permissions WHERE database_id = @databaseId AND user_id = @userId AND id = @id";
        cmd.Parameters.AddWithValue("@databaseId", databaseId);
        cmd.Parameters.AddWithValue("@userId", userId);
        cmd.Parameters.AddWithValue("@id", permissionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPermissionFromReader(reader) : null;
    }

    private static void InsertPermission(SqliteConnection connection, CosmosPermission permission)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO permissions (id, database_id, user_id, rid, etag, timestamp, permission_mode, resource, token)
            VALUES (@id, @databaseId, @userId, @rid, @etag, @timestamp, @permissionMode, @resource, @token)
        """;
        cmd.Parameters.AddWithValue("@id", permission.Id);
        cmd.Parameters.AddWithValue("@databaseId", permission.DatabaseId);
        cmd.Parameters.AddWithValue("@userId", permission.UserId);
        cmd.Parameters.AddWithValue("@rid", permission.Rid);
        cmd.Parameters.AddWithValue("@etag", permission.ETag);
        cmd.Parameters.AddWithValue("@timestamp", permission.Timestamp);
        cmd.Parameters.AddWithValue("@permissionMode", (int)permission.PermissionMode);
        cmd.Parameters.AddWithValue("@resource", permission.Resource);
        cmd.Parameters.AddWithValue("@token", (object?)permission.Token ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static CosmosPermission ReadPermissionFromReader(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var databaseId = reader.GetString(1);
        var userId = reader.GetString(2);
        return new CosmosPermission
        {
            Id = id,
            DatabaseId = databaseId,
            UserId = userId,
            Rid = reader.GetString(3),
            ETag = reader.GetString(4),
            Timestamp = reader.GetInt64(5),
            Self = $"dbs/{databaseId}/users/{userId}/permissions/{id}/",
            PermissionMode = (PermissionMode)reader.GetInt32(6),
            Resource = reader.GetString(7),
            Token = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    // ─── Offer helpers ──────────────────────────────────────────────

    private static CosmosOffer? ReadOffer(SqliteConnection connection, string offerId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, rid, etag, timestamp, offer_throughput, resource, offer_resource_id FROM offers WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", offerId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadOfferFromReader(reader) : null;
    }

    private static void CreateOfferForContainer(SqliteConnection connection, CosmosContainer container)
    {
        var offer = new CosmosOffer
        {
            Content = new OfferContent { OfferThroughput = container.MaxThroughput },
            Resource = container.Self,
            OfferResourceId = container.Rid
        };
        offer.Rid = offer.Id;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO offers (id, rid, etag, timestamp, offer_throughput, resource, offer_resource_id) VALUES (@id, @rid, @etag, @timestamp, @offerThroughput, @resource, @offerResourceId)";
        cmd.Parameters.AddWithValue("@id", offer.Id);
        cmd.Parameters.AddWithValue("@rid", offer.Rid);
        cmd.Parameters.AddWithValue("@etag", offer.ETag);
        cmd.Parameters.AddWithValue("@timestamp", offer.Timestamp);
        cmd.Parameters.AddWithValue("@offerThroughput", offer.Content.OfferThroughput);
        cmd.Parameters.AddWithValue("@resource", offer.Resource);
        cmd.Parameters.AddWithValue("@offerResourceId", offer.OfferResourceId);
        cmd.ExecuteNonQuery();
    }

    private static CosmosOffer ReadOfferFromReader(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Rid = reader.GetString(1),
        ETag = reader.GetString(2),
        Timestamp = reader.GetInt64(3),
        Content = new OfferContent { OfferThroughput = reader.GetInt32(4) },
        Resource = reader.GetString(5),
        OfferResourceId = reader.GetString(6)
    };

    // ─── Unique key enforcement ─────────────────────────────────────

    private static void EnforceUniqueKeyPolicyForContainer(
        SqliteConnection connection,
        CosmosContainer container,
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        JsonObject document,
        string? excludeDocumentId)
    {
        if (container.UniqueKeyPolicy?.UniqueKeys is not { Count: > 0 })
            return;

        var docs = ListDocumentsFromConnection(connection, databaseId, containerId);
        DocumentStoreHelpers.EnforceUniqueKeyPolicy(container, docs, partitionKey, document, excludeDocumentId);
    }

    // ─── General helpers ────────────────────────────────────────────

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, params (string name, object value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
