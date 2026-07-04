using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using HNSW.Net;

namespace Azure.Cosmos.LightEmulator.Storage.Vector;

/// <summary>
/// Maintains in-memory vector indexes (HNSW approximate or exact "flat") per
/// container embedding path and serves nearest-neighbour queries. Shards are
/// built lazily from the backing document store on first use and kept up to date
/// via the maintenance hooks invoked by <c>VectorIndexingDocumentStore</c>.
/// </summary>
public sealed class HnswVectorIndexProvider : IVectorIndexProvider
{
    private readonly IDocumentStore _store;
    private readonly VectorIndexOptions _options;
    private readonly ConcurrentDictionary<string, Lazy<Task<Shard?>>> _shards = new(StringComparer.Ordinal);

    public HnswVectorIndexProvider(IDocumentStore store, VectorIndexOptions options)
    {
        _store = store;
        _options = options;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<bool> EnsureIndexAsync(
        string databaseId,
        string containerId,
        string path,
        string indexType,
        VectorDistanceFunction distanceFunction,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        var normalized = NormalizePath(path);
        var key = ShardKey(databaseId, containerId, normalized);
        var lazy = _shards.GetOrAdd(key, _ => new Lazy<Task<Shard?>>(
            () => BuildShardAsync(databaseId, containerId, normalized, indexType, distanceFunction, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var shard = await lazy.Value.ConfigureAwait(false);
            if (shard is null)
            {
                // Not indexable (policy off / no declaration): drop the cached negative
                // so a later declaration can retry.
                _shards.TryRemove(key, out _);
                return false;
            }

            return true;
        }
        catch
        {
            _shards.TryRemove(key, out _);
            throw;
        }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(VectorSearchRequest request, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return [];
        }

        var built = await EnsureIndexAsync(
            request.DatabaseId, request.ContainerId, request.Path, request.IndexType, request.DistanceFunction, ct)
            .ConfigureAwait(false);
        if (!built)
        {
            return [];
        }

        var key = ShardKey(request.DatabaseId, request.ContainerId, NormalizePath(request.Path));
        if (!_shards.TryGetValue(key, out var lazy) || !lazy.Value.IsCompletedSuccessfully || lazy.Value.Result is not { } shard)
        {
            return [];
        }

        var query = request.QueryVector;
        if (shard.Dimensions == 0 || query.Length != shard.Dimensions)
        {
            return [];
        }

        shard.Lock.EnterReadLock();
        try
        {
            var pkHeader = request.PartitionKey?.ToHeaderString();
            var graphReady = shard.Graph is not null;

            IEnumerable<(Entry Entry, double Distance)> ranked;
            if (pkHeader is not null)
            {
                // Partition-scoped query. Small partitions (or an unbuilt graph) are
                // exact-scanned over just that partition's entries; large partitions
                // use the graph with a partition-filtered adaptive over-fetch.
                var ids = shard.PartitionEntries.TryGetValue(pkHeader, out var list) ? list : null;
                if (ids is null || ids.Count == 0)
                {
                    return [];
                }

                var liveCount = 0;
                for (var i = 0; i < ids.Count; i++)
                {
                    if (!shard.Entries[ids[i]].Deleted)
                    {
                        liveCount++;
                    }
                }

                if (!graphReady || liveCount <= _options.PartitionExactScanThreshold)
                {
                    ranked = ids
                        .Select(id => shard.Entries[id])
                        .Where(e => !e.Deleted)
                        .Select(e => (e, (double)VectorMath.NearestFirstDistance(e.Vector, query, shard.DistanceFunction)))
                        .OrderBy(t => t.Item2);
                }
                else
                {
                    ranked = PartitionFilteredGraphSearch(shard, query, pkHeader, request.TopK);
                }
            }
            else if (!graphReady)
            {
                // Whole-container exact scan (flat index or graph still building).
                ranked = shard.Entries
                    .Where(e => !e.Deleted)
                    .Select(e => (e, (double)VectorMath.NearestFirstDistance(e.Vector, query, shard.DistanceFunction)))
                    .OrderBy(t => t.Item2);
            }
            else
            {
                var k2 = (int)Math.Min(shard.Entries.Count, (long)request.TopK + shard.Tombstones);
                if (k2 <= 0)
                {
                    return [];
                }

                var results = shard.Graph!.KNNSearch(query, k2);
                ranked = results
                    .Where(r => r.Id >= 0 && r.Id < shard.Entries.Count && !shard.Entries[r.Id].Deleted)
                    .OrderBy(r => r.Distance)
                    .Select(r => (shard.Entries[r.Id], (double)r.Distance));
            }

            return ranked
                .Take(request.TopK)
                .Select(t => new VectorHit(
                    t.Entry.DocId,
                    t.Entry.Pk,
                    t.Distance,
                    VectorMath.Score(t.Entry.Vector, query, shard.DistanceFunction)))
                .ToList();
        }
        finally
        {
            shard.Lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Graph KNN restricted to a single partition. Because the HNSW graph spans all
    /// partitions, over-fetch adaptively (widening the candidate set) until at least
    /// <paramref name="topK"/> live entries from the target partition are found or the
    /// whole graph has been scanned.
    /// </summary>
    private static IReadOnlyList<(Entry Entry, double Distance)> PartitionFilteredGraphSearch(
        Shard shard, float[] query, string pkHeader, int topK)
    {
        var total = shard.Entries.Count;
        var need = Math.Max(1, topK);
        var k = (int)Math.Min(total, Math.Max((long)need * 4, 64));
        var matches = new List<(Entry Entry, double Distance)>();

        while (true)
        {
            matches.Clear();
            var results = shard.Graph!.KNNSearch(query, k);
            foreach (var r in results.OrderBy(r => r.Distance))
            {
                if (r.Id < 0 || r.Id >= shard.Entries.Count)
                {
                    continue;
                }

                var entry = shard.Entries[r.Id];
                if (!entry.Deleted && entry.PkHeader == pkHeader)
                {
                    matches.Add((entry, r.Distance));
                }
            }

            if (matches.Count >= need || k >= total)
            {
                return matches;
            }

            k = (int)Math.Min(total, (long)k * 4);
        }
    }

    public void OnUpsert(string databaseId, string containerId, CosmosDocument document)
    {
        foreach (var shard in BuiltShardsFor(databaseId, containerId))
        {
            var vector = ExtractVector(document.Body, shard.Path);
            shard.Lock.EnterWriteLock();
            try
            {
                var docKey = DocKey(document.Id, document.PartitionKey);
                if (shard.KeyToId.TryGetValue(docKey, out var oldId))
                {
                    shard.Entries[oldId].Deleted = true;
                    shard.Tombstones++;
                    shard.KeyToId.Remove(docKey);
                }

                if (vector is not null && vector.Length == shard.Dimensions && shard.Dimensions > 0)
                {
                    AppendEntry(shard, document.Id, document.PartitionKey, vector, docKey);
                }

                MaybeRebuild(shard);
            }
            finally
            {
                shard.Lock.ExitWriteLock();
            }
        }
    }

    public void OnDelete(string databaseId, string containerId, string documentId, PartitionKeyValue partitionKey)
    {
        foreach (var shard in BuiltShardsFor(databaseId, containerId))
        {
            shard.Lock.EnterWriteLock();
            try
            {
                var docKey = DocKey(documentId, partitionKey);
                if (shard.KeyToId.TryGetValue(docKey, out var oldId))
                {
                    shard.Entries[oldId].Deleted = true;
                    shard.Tombstones++;
                    shard.KeyToId.Remove(docKey);
                    MaybeRebuild(shard);
                }
            }
            finally
            {
                shard.Lock.ExitWriteLock();
            }
        }
    }

    public void OnContainerCleared(string databaseId, string containerId)
    {
        foreach (var shard in BuiltShardsFor(databaseId, containerId))
        {
            shard.Lock.EnterWriteLock();
            try
            {
                Rebuild(shard, []);
            }
            finally
            {
                shard.Lock.ExitWriteLock();
            }
        }
    }

    public void OnContainerDropped(string databaseId, string containerId)
    {
        var prefix = ShardKeyPrefix(databaseId, containerId);
        foreach (var key in _shards.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _shards.TryRemove(key, out _);
        }
    }

    private async Task<Shard?> BuildShardAsync(
        string databaseId,
        string containerId,
        string path,
        string indexType,
        VectorDistanceFunction distanceFunction,
        CancellationToken ct)
    {
        var container = await _store.GetContainerAsync(databaseId, containerId, ct).ConfigureAwait(false);

        var declaredIndex = container.IndexingPolicy.VectorIndexes?
            .FirstOrDefault(vi => PathsMatch(vi.Path, path));
        if (!_options.ImplicitIndexing && declaredIndex is null)
        {
            return null;
        }

        // Prefer explicitly declared policy over the caller-supplied defaults.
        var effectiveType = declaredIndex?.Type ?? indexType;
        var effectiveFunction = distanceFunction;
        var embedding = container.VectorEmbeddingPolicy?.VectorEmbeddings
            .FirstOrDefault(e => PathsMatch(e.Path, path));
        if (embedding is not null)
        {
            effectiveFunction = VectorDistanceFunctions.Parse(embedding.DistanceFunction);
        }

        var all = await _store.ListDocumentsAsync(databaseId, containerId, ct).ConfigureAwait(false);

        var shard = new Shard
        {
            Path = path,
            DistanceFunction = effectiveFunction,
            IndexType = effectiveType,
            Dimensions = 0
        };

        var vectors = new List<float[]>();
        foreach (var doc in all.Resources)
        {
            var vector = ExtractVector(doc.Body, path);
            if (vector is null)
            {
                continue;
            }

            if (shard.Dimensions == 0)
            {
                shard.Dimensions = vector.Length;
            }
            else if (vector.Length != shard.Dimensions)
            {
                continue;
            }

            var entry = new Entry
            {
                DocId = doc.Id,
                Pk = doc.PartitionKey,
                PkHeader = doc.PartitionKey.ToHeaderString(),
                Vector = vector
            };
            var id = shard.Entries.Count;
            shard.KeyToId[DocKey(doc.Id, doc.PartitionKey)] = id;
            shard.Entries.Add(entry);
            AddToPartition(shard, entry.PkHeader, id);
            vectors.Add(vector);
        }

        if (!IsFlat(effectiveType) && vectors.Count > 0)
        {
            if (_options.BackgroundBuild)
            {
                // Return immediately with entries populated so queries can exact-scan
                // while the graph builds off-thread, then switch to acceleration.
                var generation = shard.BuildGeneration;
                _ = Task.Run(() => BuildGraphInBackground(shard, vectors, effectiveFunction, generation));
            }
            else
            {
                shard.Graph = CreateGraph(effectiveFunction);
                shard.Graph.AddItems(vectors);
            }
        }

        return shard;
    }

    /// <summary>
    /// Builds the HNSW graph for a shard off the query thread. The expensive
    /// <c>AddItems</c> runs without the shard lock; the finished graph (plus any
    /// entries appended while building) is published under the write lock, unless a
    /// rebuild has since invalidated the snapshot.
    /// </summary>
    private void BuildGraphInBackground(
        Shard shard, List<float[]> snapshot, VectorDistanceFunction fn, int generation)
    {
        try
        {
            var graph = CreateGraph(fn);
            graph.AddItems(snapshot);

            shard.Lock.EnterWriteLock();
            try
            {
                if (shard.BuildGeneration != generation)
                {
                    // A rebuild happened while we were building; discard this graph.
                    return;
                }

                // Add any live entries appended after the snapshot was taken so the
                // graph's item ids stay aligned with shard.Entries indices.
                if (shard.Entries.Count > snapshot.Count)
                {
                    var pending = new List<float[]>(shard.Entries.Count - snapshot.Count);
                    for (var i = snapshot.Count; i < shard.Entries.Count; i++)
                    {
                        pending.Add(shard.Entries[i].Vector);
                    }

                    if (pending.Count > 0)
                    {
                        graph.AddItems(pending);
                    }
                }

                shard.Graph = graph;
            }
            finally
            {
                shard.Lock.ExitWriteLock();
            }
        }
        catch
        {
            // Leave the shard graph-less; queries continue to exact-scan.
        }
    }

    private static void AddToPartition(Shard shard, string pkHeader, int id)
    {
        if (!shard.PartitionEntries.TryGetValue(pkHeader, out var list))
        {
            list = [];
            shard.PartitionEntries[pkHeader] = list;
        }

        list.Add(id);
    }

    private SmallWorld<float[], float> CreateGraph(VectorDistanceFunction distanceFunction)
    {
        var parameters = new SmallWorldParameters
        {
            M = _options.M,
            LevelLambda = 1.0 / Math.Log(Math.Max(2, _options.M)),
            EfSearch = _options.EfSearch,
            ConstructionPruning = _options.EfConstruction,
            NeighbourHeuristic = NeighbourSelectionHeuristic.SelectHeuristic
        };

        return new SmallWorld<float[], float>(
            Metric(distanceFunction), DefaultRandomGenerator.Instance, parameters, threadSafe: true);
    }

    private static Func<float[], float[], float> Metric(VectorDistanceFunction fn) => fn switch
    {
        VectorDistanceFunction.Cosine => CosineDistance.SIMD,
        VectorDistanceFunction.DotProduct => static (a, b) => (float)(-VectorMath.DotProduct(a, b)),
        VectorDistanceFunction.Euclidean => static (a, b) => (float)VectorMath.EuclideanDistance(a, b),
        _ => CosineDistance.SIMD
    };

    private static void AppendEntry(Shard shard, string docId, PartitionKeyValue pk, float[] vector, string docKey)
    {
        var id = shard.Entries.Count;
        var pkHeader = pk.ToHeaderString();
        shard.Entries.Add(new Entry
        {
            DocId = docId,
            Pk = pk,
            PkHeader = pkHeader,
            Vector = vector
        });
        shard.KeyToId[docKey] = id;
        AddToPartition(shard, pkHeader, id);
        shard.Graph?.AddItems(new[] { vector });
    }

    private void MaybeRebuild(Shard shard)
    {
        if (shard.Tombstones <= 0)
        {
            return;
        }

        var live = shard.Entries.Count - shard.Tombstones;
        if (live <= 0)
        {
            Rebuild(shard, []);
            return;
        }

        if (shard.Tombstones >= 32 && shard.Tombstones >= shard.Entries.Count * _options.RebuildTombstoneRatio)
        {
            Rebuild(shard, shard.Entries.Where(e => !e.Deleted).ToList());
        }
    }

    private void Rebuild(Shard shard, IReadOnlyList<Entry> liveEntries)
    {
        shard.Entries.Clear();
        shard.KeyToId.Clear();
        shard.PartitionEntries.Clear();
        shard.Tombstones = 0;
        shard.Graph = null;

        // Invalidate any in-flight background graph build from a prior generation.
        shard.BuildGeneration++;

        var vectors = new List<float[]>(liveEntries.Count);
        foreach (var e in liveEntries)
        {
            e.Deleted = false;
            var id = shard.Entries.Count;
            shard.KeyToId[DocKey(e.DocId, e.Pk)] = id;
            shard.Entries.Add(e);
            AddToPartition(shard, e.PkHeader, id);
            vectors.Add(e.Vector);
        }

        if (!IsFlat(shard.IndexType) && vectors.Count > 0)
        {
            if (_options.BackgroundBuild)
            {
                var generation = shard.BuildGeneration;
                _ = Task.Run(() => BuildGraphInBackground(shard, vectors, shard.DistanceFunction, generation));
            }
            else
            {
                shard.Graph = CreateGraph(shard.DistanceFunction);
                shard.Graph.AddItems(vectors);
            }
        }
    }

    private IEnumerable<Shard> BuiltShardsFor(string databaseId, string containerId)
    {
        var prefix = ShardKeyPrefix(databaseId, containerId);
        foreach (var pair in _shards)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (pair.Value.IsValueCreated && pair.Value.Value.IsCompletedSuccessfully && pair.Value.Value.Result is { } shard)
            {
                yield return shard;
            }
        }
    }

    private static float[]? ExtractVector(JsonObject body, string path)
    {
        JsonNode? node = body;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out node) || node is null)
            {
                return null;
            }
        }

        if (node is not JsonArray array || array.Count == 0)
        {
            return null;
        }

        var vector = new float[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonValue value && TryGetFloat(value, out var f))
            {
                vector[i] = f;
            }
            else
            {
                return null;
            }
        }

        return vector;
    }

    private static bool TryGetFloat(JsonValue value, out float result)
    {
        // JsonValue numbers may be backed by different CLR types (int/long/double/
        // decimal) depending on whether the document was freshly written or
        // round-tripped through storage. Handle all numeric representations.
        if (value.TryGetValue<double>(out var d))
        {
            result = (float)d;
            return true;
        }

        if (value.TryGetValue<long>(out var l))
        {
            result = l;
            return true;
        }

        if (value.TryGetValue<int>(out var iv))
        {
            result = iv;
            return true;
        }

        if (value.TryGetValue<decimal>(out var dec))
        {
            result = (float)dec;
            return true;
        }

        if (value.TryGetValue<float>(out var fv))
        {
            result = fv;
            return true;
        }

        if (value.TryGetValue<System.Text.Json.JsonElement>(out var element)
            && element.ValueKind == System.Text.Json.JsonValueKind.Number
            && element.TryGetDouble(out var ed))
        {
            result = (float)ed;
            return true;
        }

        result = 0f;
        return false;
    }

    private static bool IsFlat(string indexType) =>
        string.Equals(indexType, "flat", StringComparison.OrdinalIgnoreCase);

    private static bool PathsMatch(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.Ordinal);

    private static string NormalizePath(string path) => "/" + path.Trim().TrimStart('/');

    private static string DocKey(string docId, PartitionKeyValue pk) => pk.ToHeaderString() + "\0" + docId;

    private static string ShardKey(string databaseId, string containerId, string path) =>
        databaseId + "\0" + containerId + "\0" + path;

    private static string ShardKeyPrefix(string databaseId, string containerId) =>
        databaseId + "\0" + containerId + "\0";

    private sealed class Shard
    {
        public required string Path { get; init; }
        public required VectorDistanceFunction DistanceFunction { get; init; }
        public required string IndexType { get; init; }
        public required int Dimensions { get; set; }
        public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.NoRecursion);
        public SmallWorld<float[], float>? Graph { get; set; }
        public List<Entry> Entries { get; } = [];
        public Dictionary<string, int> KeyToId { get; } = new(StringComparer.Ordinal);

        /// <summary>Maps a partition-key header string to the ids of its entries.</summary>
        public Dictionary<string, List<int>> PartitionEntries { get; } = new(StringComparer.Ordinal);
        public int Tombstones { get; set; }

        /// <summary>
        /// Incremented on every full rebuild so an in-flight background graph build
        /// can detect that its snapshot is stale and discard its result.
        /// </summary>
        public int BuildGeneration { get; set; }
    }

    private sealed class Entry
    {
        public required string DocId { get; init; }
        public required PartitionKeyValue Pk { get; init; }
        public required string PkHeader { get; init; }
        public required float[] Vector { get; init; }
        public bool Deleted { get; set; }
    }
}
