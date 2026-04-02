using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using static Azure.Cosmos.LightEmulator.Storage.DocumentStoreHelpers;

namespace Azure.Cosmos.LightEmulator.Storage.InMemory;

/// <summary>
/// Fully in-memory implementation of <see cref="IDocumentStore"/> backed by ConcurrentDictionary collections.
/// </summary>
public class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<string, CosmosDatabase> _databases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CosmosContainer> _containers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CosmosDocument> _documents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CosmosUser> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CosmosPermission> _permissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CosmosOffer> _offers = new(StringComparer.Ordinal);

    private long _globalLsn;
    private readonly IChangeFeedProvider _changeFeed;

    public InMemoryDocumentStore(IChangeFeedProvider changeFeed)
    {
        _changeFeed = changeFeed;
    }

    // ─── Database operations ────────────────────────────────────────

    public Task<CosmosDatabase> CreateDatabaseAsync(string id, CancellationToken ct = default)
    {
        var database = new CosmosDatabase { Id = id };
        if (!_databases.TryAdd(id, database))
            throw CosmosEmulatorException.Conflict("Database", id);
        return Task.FromResult(database);
    }

    public Task<CosmosDatabase> GetDatabaseAsync(string id, CancellationToken ct = default)
    {
        if (!_databases.TryGetValue(id, out var database))
            throw CosmosEmulatorException.NotFound("Database", id);
        return Task.FromResult(database);
    }

    public Task<FeedResponse<CosmosDatabase>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var resources = _databases.Values
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(new FeedResponse<CosmosDatabase> { Resources = resources });
    }

    public Task<CosmosDatabase> ReplaceDatabaseAsync(CosmosDatabase database, CancellationToken ct = default)
    {
        if (!_databases.TryGetValue(database.Id, out var existing))
            throw CosmosEmulatorException.NotFound("Database", database.Id);

        var updated = new CosmosDatabase
        {
            Id = database.Id,
            Rid = existing.Rid,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MaxThroughput = database.MaxThroughput
        };

        _databases[database.Id] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteDatabaseAsync(string id, CancellationToken ct = default)
    {
        if (!_databases.TryRemove(id, out _))
            throw CosmosEmulatorException.NotFound("Database", id);

        // Cascade delete containers and their offers
        foreach (var key in _containers.Keys.Where(k => k.StartsWith($"{id}/", StringComparison.Ordinal)).ToList())
        {
            if (_containers.TryRemove(key, out var container))
                DeleteOfferForContainer(container.Rid);
        }

        // Cascade delete documents
        foreach (var key in _documents.Keys.Where(k => k.StartsWith($"{id}/", StringComparison.Ordinal)).ToList())
            _documents.TryRemove(key, out _);

        // Cascade delete permissions for users in this database
        foreach (var key in _permissions.Keys.Where(k => k.StartsWith($"{id}/", StringComparison.Ordinal)).ToList())
            _permissions.TryRemove(key, out _);

        // Cascade delete users
        foreach (var key in _users.Keys.Where(k => k.StartsWith($"{id}/", StringComparison.Ordinal)).ToList())
            _users.TryRemove(key, out _);

        return Task.CompletedTask;
    }

    // ─── Container operations ───────────────────────────────────────

    public Task<CosmosContainer> CreateContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default)
    {
        if (!_databases.ContainsKey(databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        var containerKey = MakeContainerKey(databaseId, container.Id);
        container.DatabaseId = databaseId;
        container.Self = $"dbs/{databaseId}/colls/{container.Id}/";

        if (!_containers.TryAdd(containerKey, container))
            throw CosmosEmulatorException.Conflict("Container", container.Id);

        CreateOfferForContainer(container);
        return Task.FromResult(container);
    }

    public Task<CosmosContainer> GetContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        if (!_containers.TryGetValue(MakeContainerKey(databaseId, containerId), out var container))
            throw CosmosEmulatorException.NotFound("Container", containerId);
        return Task.FromResult(container);
    }

    public Task<FeedResponse<CosmosContainer>> ListContainersAsync(string databaseId, CancellationToken ct = default)
    {
        if (!_databases.ContainsKey(databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        var prefix = $"{databaseId}/";
        var resources = _containers.Values
            .Where(c => string.Equals(c.DatabaseId, databaseId, StringComparison.Ordinal))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(new FeedResponse<CosmosContainer> { Resources = resources });
    }

    public Task<CosmosContainer> ReplaceContainerAsync(string databaseId, CosmosContainer container, CancellationToken ct = default)
    {
        var containerKey = MakeContainerKey(databaseId, container.Id);
        if (!_containers.TryGetValue(containerKey, out var existing))
            throw CosmosEmulatorException.NotFound("Container", container.Id);

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
            VectorEmbeddingPolicy = container.VectorEmbeddingPolicy,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/colls/{container.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        _containers[containerKey] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var containerKey = MakeContainerKey(databaseId, containerId);
        if (!_containers.TryRemove(containerKey, out var container))
            throw CosmosEmulatorException.NotFound("Container", containerId);

        // Cascade delete documents
        var docPrefix = $"{databaseId}/{containerId}/";
        foreach (var key in _documents.Keys.Where(k => k.StartsWith(docPrefix, StringComparison.Ordinal)).ToList())
            _documents.TryRemove(key, out _);

        DeleteOfferForContainer(container.Rid);
        return Task.CompletedTask;
    }

    // ─── Document operations ────────────────────────────────────────

    public async Task<CosmosDocument> CreateDocumentAsync(string databaseId, string containerId, JsonObject document, bool? isIndexed = null, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(databaseId, containerId, ct);
        var id = document["id"]?.GetValue<string>()
                 ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

        EnforceDocumentSizeLimit(document);

        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var documentKey = MakeDocumentKey(databaseId, containerId, id, partitionKey);

        if (_documents.ContainsKey(documentKey))
            throw CosmosEmulatorException.Conflict("Document", id);

        EnforceUniqueKeys(container, databaseId, containerId, partitionKey, document, null);

        var created = new CosmosDocument
        {
            Id = id,
            DatabaseId = databaseId,
            ContainerId = containerId,
            PartitionKey = partitionKey,
            Body = document.DeepClone().AsObject(),
            TimeToLive = ExtractTimeToLive(document),
            Lsn = GetNextLsn(),
            Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/",
            IsIndexed = isIndexed ?? true
        };

        if (!_documents.TryAdd(documentKey, created))
            throw CosmosEmulatorException.Conflict("Document", id);

        await _changeFeed.RecordChangeAsync(databaseId, containerId, created, ChangeType.Create, ct: ct);
        return created;
    }

    public Task<CosmosDocument> ReadDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        var documentKey = MakeDocumentKey(databaseId, containerId, documentId, partitionKey);
        if (!_documents.TryGetValue(documentKey, out var document))
            throw CosmosEmulatorException.NotFound("Document", documentId);
        return Task.FromResult(document);
    }

    public async Task<CosmosDocument> ReplaceDocumentAsync(string databaseId, string containerId, string documentId, JsonObject document, string? ifMatch = null, bool? isIndexed = null, CancellationToken ct = default)
    {
        EnforceDocumentSizeLimit(document);

        var container = await GetContainerAsync(databaseId, containerId, ct);
        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var documentKey = MakeDocumentKey(databaseId, containerId, documentId, partitionKey);

        if (!_documents.TryGetValue(documentKey, out var existing))
            throw CosmosEmulatorException.NotFound("Document", documentId);

        if (ifMatch is not null && existing.ETag != ifMatch)
            throw CosmosEmulatorException.PreconditionFailed($"ETag mismatch. Expected: {ifMatch}, Actual: {existing.ETag}");

        EnforceUniqueKeys(container, databaseId, containerId, partitionKey, document, documentId);

        var updated = new CosmosDocument
        {
            Id = documentId,
            Rid = existing.Rid,
            DatabaseId = databaseId,
            ContainerId = containerId,
            PartitionKey = partitionKey,
            Body = document.DeepClone().AsObject(),
            TimeToLive = ExtractTimeToLive(document),
            Lsn = GetNextLsn(),
            Self = existing.Self,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            IsIndexed = isIndexed ?? existing.IsIndexed
        };

        _documents[documentKey] = updated;
        await _changeFeed.RecordChangeAsync(databaseId, containerId, updated, ChangeType.Replace, existing, ct);
        return updated;
    }

    public async Task<CosmosDocument> UpsertDocumentAsync(string databaseId, string containerId, JsonObject document, bool? isIndexed = null, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(databaseId, containerId, ct);
        var id = document["id"]?.GetValue<string>()
                 ?? throw CosmosEmulatorException.BadRequest("Document must have an 'id' property.");

        var partitionKey = ExtractPartitionKey(document, container.PartitionKey);
        var documentKey = MakeDocumentKey(databaseId, containerId, id, partitionKey);

        if (_documents.ContainsKey(documentKey))
            return await ReplaceDocumentAsync(databaseId, containerId, id, document, isIndexed: isIndexed, ct: ct);

        return await CreateDocumentAsync(databaseId, containerId, document, isIndexed, ct);
    }

    public async Task<CosmosDocument> PatchDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, IReadOnlyList<PatchOperation> operations, string? ifMatch = null, string? condition = null, CancellationToken ct = default)
    {
        var documentKey = MakeDocumentKey(databaseId, containerId, documentId, partitionKey);
        if (!_documents.TryGetValue(documentKey, out var existing))
            throw CosmosEmulatorException.NotFound("Document", documentId);

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
            Lsn = GetNextLsn(),
            Self = existing.Self,
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        _documents[documentKey] = updated;
        await _changeFeed.RecordChangeAsync(databaseId, containerId, updated, ChangeType.Replace, existing, ct);
        return updated;
    }

    public async Task DeleteDocumentAsync(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey, CancellationToken ct = default)
    {
        var documentKey = MakeDocumentKey(databaseId, containerId, documentId, partitionKey);
        if (!_documents.TryRemove(documentKey, out var removed))
            throw CosmosEmulatorException.NotFound("Document", documentId);

        removed.Lsn = GetNextLsn();
        removed.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _changeFeed.RecordChangeAsync(databaseId, containerId, removed, ChangeType.Delete, ct: ct);
    }

    public async Task<int> EmptyContainerAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        await GetContainerAsync(databaseId, containerId, ct);

        var docPrefix = $"{databaseId}/{containerId}/";
        var keysToRemove = _documents.Keys
            .Where(k => k.StartsWith(docPrefix, StringComparison.Ordinal))
            .ToList();

        var count = 0;
        foreach (var key in keysToRemove)
        {
            if (_documents.TryRemove(key, out var removed))
            {
                removed.Lsn = GetNextLsn();
                removed.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _changeFeed.RecordChangeAsync(databaseId, containerId, removed, ChangeType.Delete, ct: ct);
                count++;
            }
        }

        return count;
    }

    public Task<long> GetGlobalLsnAsync(CancellationToken ct = default) =>
        Task.FromResult(Interlocked.Read(ref _globalLsn));

    // ─── Bulk / Read-many ───────────────────────────────────────────

    public Task<FeedResponse<CosmosDocument>> ReadManyDocumentsAsync(string databaseId, string containerId, IEnumerable<(string id, PartitionKeyValue pk)> items, CancellationToken ct = default)
    {
        var documents = new List<CosmosDocument>();
        foreach (var (id, pk) in items)
        {
            var key = MakeDocumentKey(databaseId, containerId, id, pk);
            if (_documents.TryGetValue(key, out var doc))
                documents.Add(doc);
        }

        return Task.FromResult(new FeedResponse<CosmosDocument> { Resources = documents });
    }

    public Task<FeedResponse<CosmosDocument>> ListDocumentsAsync(string databaseId, string containerId, CancellationToken ct = default)
    {
        var docPrefix = $"{databaseId}/{containerId}/";
        var resources = _documents.Values
            .Where(d => string.Equals(d.DatabaseId, databaseId, StringComparison.Ordinal)
                     && string.Equals(d.ContainerId, containerId, StringComparison.Ordinal))
            .OrderBy(d => d.Timestamp)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(new FeedResponse<CosmosDocument> { Resources = resources });
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

        // Snapshots for rollback: key → original document (null if didn't exist)
        var snapshots = new Dictionary<string, CosmosDocument?>(StringComparer.Ordinal);
        var createdKeys = new List<string>();
        var results = new List<BatchOperationResponse>();
        var changeFeedEntries = new List<(CosmosDocument doc, ChangeType type, CosmosDocument? previous)>();
        var failedIndex = -1;

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            try
            {
                var result = ExecuteBatchOperation(
                    databaseId, containerId, partitionKey, container,
                    op, snapshots, createdKeys, changeFeedEntries);
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
            RollbackBatch(snapshots, createdKeys);

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
            foreach (var (doc, type, previous) in changeFeedEntries)
                await _changeFeed.RecordChangeAsync(databaseId, containerId, doc, type, previous, ct);
        }

        return results;
    }

    private BatchOperationResponse ExecuteBatchOperation(
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        CosmosContainer container,
        BatchOperationRequest op,
        Dictionary<string, CosmosDocument?> snapshots,
        List<string> createdKeys,
        List<(CosmosDocument doc, ChangeType type, CosmosDocument? previous)> changeFeedEntries)
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
                var documentKey = MakeDocumentKey(databaseId, containerId, id, docPk);

                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = _documents.GetValueOrDefault(documentKey);

                if (_documents.ContainsKey(documentKey))
                    throw CosmosEmulatorException.Conflict("Document", id);

                var created = new CosmosDocument
                {
                    Id = id,
                    DatabaseId = databaseId,
                    ContainerId = containerId,
                    PartitionKey = docPk,
                    Body = body.DeepClone().AsObject(),
                    TimeToLive = ExtractTimeToLive(body),
                    Lsn = GetNextLsn(),
                    Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/"
                };

                _documents[documentKey] = created;
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
                var documentKey = MakeDocumentKey(databaseId, containerId, id, partitionKey);
                if (!_documents.TryGetValue(documentKey, out var doc))
                    throw CosmosEmulatorException.NotFound("Document", id);
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
                var documentKey = MakeDocumentKey(databaseId, containerId, id, docPk);

                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = _documents.GetValueOrDefault(documentKey);

                if (!_documents.TryGetValue(documentKey, out var existing))
                    throw CosmosEmulatorException.NotFound("Document", id);

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
                    Lsn = GetNextLsn(),
                    Self = existing.Self,
                    ETag = ETagGenerator.Generate(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                _documents[documentKey] = updated;
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
                var documentKey = MakeDocumentKey(databaseId, containerId, id, docPk);

                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = _documents.GetValueOrDefault(documentKey);

                _documents.TryGetValue(documentKey, out var existing);

                EnforceUniqueKeys(container, databaseId, containerId, docPk, body, existing is not null ? id : null);

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
                        Lsn = GetNextLsn(),
                        Self = existing.Self,
                        ETag = ETagGenerator.Generate(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    _documents[documentKey] = updated;
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
                        Lsn = GetNextLsn(),
                        Self = $"dbs/{databaseId}/colls/{containerId}/docs/{id}/"
                    };

                    _documents[documentKey] = created;
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
                var documentKey = MakeDocumentKey(databaseId, containerId, id, partitionKey);

                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = _documents.GetValueOrDefault(documentKey);

                if (!_documents.TryRemove(documentKey, out var existing))
                    throw CosmosEmulatorException.NotFound("Document", id);

                existing.Lsn = GetNextLsn();
                existing.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

                var documentKey = MakeDocumentKey(databaseId, containerId, id, partitionKey);

                if (!snapshots.ContainsKey(documentKey))
                    snapshots[documentKey] = _documents.GetValueOrDefault(documentKey);

                if (!_documents.TryGetValue(documentKey, out var existing))
                    throw CosmosEmulatorException.NotFound("Document", id);

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
                    Lsn = GetNextLsn(),
                    Self = existing.Self,
                    ETag = ETagGenerator.Generate(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                _documents[documentKey] = updated;
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

    private void RollbackBatch(
        Dictionary<string, CosmosDocument?> snapshots,
        List<string> createdKeys)
    {
        // Delete newly created records
        foreach (var key in createdKeys)
            _documents.TryRemove(key, out _);

        // Restore modified records to their original state
        foreach (var (key, snapshot) in snapshots)
        {
            if (createdKeys.Contains(key))
                continue;

            if (snapshot is not null)
                _documents[key] = snapshot;
        }
    }

    // ─── User operations ────────────────────────────────────────────

    public Task<CosmosUser> CreateUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        if (!_databases.ContainsKey(databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        var key = MakeUserKey(databaseId, userId);
        var user = new CosmosUser
        {
            Id = userId,
            DatabaseId = databaseId,
            Self = $"dbs/{databaseId}/users/{userId}/"
        };

        if (!_users.TryAdd(key, user))
            throw CosmosEmulatorException.Conflict("User", userId);

        return Task.FromResult(user);
    }

    public Task<CosmosUser> GetUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        if (!_users.TryGetValue(MakeUserKey(databaseId, userId), out var user))
            throw CosmosEmulatorException.NotFound("User", userId);
        return Task.FromResult(user);
    }

    public Task<FeedResponse<CosmosUser>> ListUsersAsync(string databaseId, CancellationToken ct = default)
    {
        if (!_databases.ContainsKey(databaseId))
            throw CosmosEmulatorException.NotFound("Database", databaseId);

        var resources = _users.Values
            .Where(u => string.Equals(u.DatabaseId, databaseId, StringComparison.Ordinal))
            .OrderBy(u => u.Id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(new FeedResponse<CosmosUser> { Resources = resources });
    }

    public Task<CosmosUser> ReplaceUserAsync(string databaseId, CosmosUser user, CancellationToken ct = default)
    {
        var key = MakeUserKey(databaseId, user.Id);
        if (!_users.TryGetValue(key, out var existing))
            throw CosmosEmulatorException.NotFound("User", user.Id);

        var updated = new CosmosUser
        {
            Id = user.Id,
            DatabaseId = databaseId,
            Rid = existing.Rid,
            Self = $"dbs/{databaseId}/users/{user.Id}/",
            ETag = ETagGenerator.Generate(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        _users[key] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteUserAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        var key = MakeUserKey(databaseId, userId);
        if (!_users.TryRemove(key, out _))
            throw CosmosEmulatorException.NotFound("User", userId);

        // Cascade delete permissions
        var permPrefix = $"{databaseId}/{userId}/";
        foreach (var permKey in _permissions.Keys.Where(k => k.StartsWith(permPrefix, StringComparison.Ordinal)).ToList())
            _permissions.TryRemove(permKey, out _);

        return Task.CompletedTask;
    }

    // ─── Permission operations ──────────────────────────────────────

    public Task<CosmosPermission> CreatePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default)
    {
        if (!_users.ContainsKey(MakeUserKey(databaseId, userId)))
            throw CosmosEmulatorException.NotFound("User", userId);

        var key = MakePermissionKey(databaseId, userId, permission.Id);
        permission.DatabaseId = databaseId;
        permission.UserId = userId;
        permission.Self = $"dbs/{databaseId}/users/{userId}/permissions/{permission.Id}/";
        permission.Token = GenerateResourceToken(permission);

        if (!_permissions.TryAdd(key, permission))
            throw CosmosEmulatorException.Conflict("Permission", permission.Id);

        return Task.FromResult(permission);
    }

    public Task<CosmosPermission> GetPermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default)
    {
        if (!_permissions.TryGetValue(MakePermissionKey(databaseId, userId, permissionId), out var permission))
            throw CosmosEmulatorException.NotFound("Permission", permissionId);
        return Task.FromResult(permission);
    }

    public Task<FeedResponse<CosmosPermission>> ListPermissionsAsync(string databaseId, string userId, CancellationToken ct = default)
    {
        if (!_users.ContainsKey(MakeUserKey(databaseId, userId)))
            throw CosmosEmulatorException.NotFound("User", userId);

        var resources = _permissions.Values
            .Where(p => string.Equals(p.DatabaseId, databaseId, StringComparison.Ordinal)
                     && string.Equals(p.UserId, userId, StringComparison.Ordinal))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(new FeedResponse<CosmosPermission> { Resources = resources });
    }

    public Task<CosmosPermission> ReplacePermissionAsync(string databaseId, string userId, CosmosPermission permission, CancellationToken ct = default)
    {
        var key = MakePermissionKey(databaseId, userId, permission.Id);
        if (!_permissions.TryGetValue(key, out var existing))
            throw CosmosEmulatorException.NotFound("Permission", permission.Id);

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

        _permissions[key] = updated;
        return Task.FromResult(updated);
    }

    public Task DeletePermissionAsync(string databaseId, string userId, string permissionId, CancellationToken ct = default)
    {
        var key = MakePermissionKey(databaseId, userId, permissionId);
        if (!_permissions.TryRemove(key, out _))
            throw CosmosEmulatorException.NotFound("Permission", permissionId);
        return Task.CompletedTask;
    }

    // ─── Offer operations ───────────────────────────────────────────

    public Task<CosmosOffer> GetOfferAsync(string offerId, CancellationToken ct = default)
    {
        if (!_offers.TryGetValue(offerId, out var offer))
            throw CosmosEmulatorException.NotFound("Offer", offerId);
        return Task.FromResult(offer);
    }

    public Task<FeedResponse<CosmosOffer>> ListOffersAsync(CancellationToken ct = default)
    {
        var resources = _offers.Values
            .OrderBy(o => o.Id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(new FeedResponse<CosmosOffer> { Resources = resources });
    }

    public Task<CosmosOffer> ReplaceOfferAsync(CosmosOffer offer, CancellationToken ct = default)
    {
        if (!_offers.TryGetValue(offer.Id, out var existing))
            throw CosmosEmulatorException.NotFound("Offer", offer.Id);

        existing.Content.OfferThroughput = offer.Content.OfferThroughput;
        existing.ETag = ETagGenerator.Generate();
        existing.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _offers[offer.Id] = existing;
        return Task.FromResult(existing);
    }

    // ─── Private helpers ────────────────────────────────────────────

    private long GetNextLsn() => Interlocked.Increment(ref _globalLsn);

    private void CreateOfferForContainer(CosmosContainer container)
    {
        var offer = new CosmosOffer
        {
            Content = new OfferContent { OfferThroughput = container.MaxThroughput },
            Resource = container.Self,
            OfferResourceId = container.Rid
        };
        offer.Rid = offer.Id;
        _offers[offer.Id] = offer;
    }

    private void DeleteOfferForContainer(string containerRid)
    {
        foreach (var key in _offers.Keys.ToList())
        {
            if (_offers.TryGetValue(key, out var offer)
                && string.Equals(offer.OfferResourceId, containerRid, StringComparison.Ordinal))
            {
                _offers.TryRemove(key, out _);
            }
        }
    }

    private void EnforceUniqueKeys(
        CosmosContainer container,
        string databaseId,
        string containerId,
        PartitionKeyValue partitionKey,
        JsonObject document,
        string? excludeDocumentId)
    {
        if (container.UniqueKeyPolicy?.UniqueKeys is not { Count: > 0 })
            return;

        var partitionDocs = _documents.Values
            .Where(d => string.Equals(d.DatabaseId, databaseId, StringComparison.Ordinal)
                     && string.Equals(d.ContainerId, containerId, StringComparison.Ordinal));

        EnforceUniqueKeyPolicy(container, partitionDocs, partitionKey, document, excludeDocumentId);
    }

    private static string MakeContainerKey(string databaseId, string containerId) =>
        $"{databaseId}/{containerId}";

    private static string MakeDocumentKey(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey) =>
        $"{databaseId}/{containerId}/{partitionKey.ToHeaderString()}/{documentId}";

    private static string MakeUserKey(string databaseId, string userId) =>
        $"{databaseId}/{userId}";

    private static string MakePermissionKey(string databaseId, string userId, string permissionId) =>
        $"{databaseId}/{userId}/{permissionId}";
}
