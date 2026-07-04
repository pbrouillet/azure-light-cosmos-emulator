using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using Azure.Cosmos.LightEmulator.Storage.Vector;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

/// <summary>
/// Integration tests for index-accelerated vector search: the query engine wired
/// with a real <see cref="HnswVectorIndexProvider"/> behind the
/// <see cref="VectorIndexingDocumentStore"/> decorator.
/// </summary>
public class VectorIndexAcceleratedTests
{
    [Fact]
    public async Task OrderByVectorDistance_Cosine_ReturnsNearestFirst_WithScores()
    {
        var (store, engine, _) = CreateSut("cosine");
        await SeedAsync(store,
            ("doc-far", new JsonArray(0, 1, 0)),
            ("doc-near", new JsonArray(1, 0, 0)),
            ("doc-mid", new JsonArray(0.9, 0.1, 0)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 3 c.id, VectorDistance(c.embedding, [1, 0, 0]) AS score FROM c " +
            "ORDER BY VectorDistance(c.embedding, [1, 0, 0])");

        result.Resources.Should().HaveCount(3);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-near");
        result.Resources[1]["id"]!.GetValue<string>().Should().Be("doc-mid");
        result.Resources[2]["id"]!.GetValue<string>().Should().Be("doc-far");
        // Cosmos VectorDistance for cosine is the similarity value; nearest (ascending) first.
        result.Resources[0]["score"]!.GetValue<double>().Should().BeApproximately(1.0, 0.0001);
        result.Resources[2]["score"]!.GetValue<double>().Should().BeApproximately(0.0, 0.0001);
    }

    [Fact]
    public async Task OrderByVectorDistance_Top1_ReturnsSingleNearest()
    {
        var (store, engine, _) = CreateSut("cosine");
        await SeedAsync(store,
            ("a", new JsonArray(0, 1, 0)),
            ("b", new JsonArray(1, 0, 0)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 1 c.id FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0])");

        result.Resources.Should().ContainSingle();
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public async Task OrderByVectorDistance_ExcludesDocumentsWithoutEmbedding()
    {
        var (store, engine, _) = CreateSut("cosine");
        await SeedAsync(store,
            ("with", new JsonArray(1, 0, 0)),
            ("without", null));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 10 c.id FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0])");

        result.Resources.Should().ContainSingle();
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("with");
    }

    [Fact]
    public async Task IndexReflectsUpdatesAndDeletes()
    {
        var (store, engine, _) = CreateSut("cosine");
        await SeedAsync(store,
            ("a", new JsonArray(1, 0, 0)),
            ("b", new JsonArray(0, 1, 0)));

        // Warm up so the shard is built and later mutations exercise the
        // incremental OnUpsert / OnDelete maintenance path (not a fresh rebuild).
        var warm = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 5 c.id FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0])");
        warm.Resources[0]["id"]!.GetValue<string>().Should().Be("a");

        // Update "b" to be the closest to the query.
        await store.ReplaceDocumentAsync("db", "coll", "b",
            NewDoc("b", new JsonArray(1, 0, 0)));
        // Delete "a".
        await store.DeleteDocumentAsync("db", "coll", "a", PartitionKeyValue.Create("t1"));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 5 c.id FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0])");

        result.Resources.Should().ContainSingle();
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public async Task Euclidean_OrderByAscending_ReturnsNearestFirst()
    {
        var (store, engine, _) = CreateSut("euclidean");
        await SeedAsync(store,
            ("far", new JsonArray(10, 10, 10)),
            ("near", new JsonArray(0, 0, 1)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 2 c.id, VectorDistance(c.embedding, [0, 0, 0]) AS score FROM c " +
            "ORDER BY VectorDistance(c.embedding, [0, 0, 0])");

        result.Resources.Should().HaveCount(2);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("near");
        result.Resources[1]["id"]!.GetValue<string>().Should().Be("far");
    }

    [Fact]
    public async Task IndexTopK_MatchesBruteForce_HighRecall()
    {
        const int dims = 24;
        const int count = 600;
        const int k = 10;
        var rng = new Random(1234);

        var (store, engine, _) = CreateSut("cosine", dimensions: dims,
            options: new VectorIndexOptions { BackgroundBuild = false });

        var vectors = new List<(string Id, double[] Vec)>();
        var docs = new List<(string, JsonArray?)>();
        for (var i = 0; i < count; i++)
        {
            var v = new double[dims];
            for (var d = 0; d < dims; d++)
            {
                v[d] = rng.NextDouble() * 2 - 1;
            }

            var id = $"doc-{i:D4}";
            vectors.Add((id, v));
            var arr = new JsonArray();
            foreach (var x in v)
            {
                arr.Add(x);
            }

            docs.Add((id, arr));
        }

        await SeedAsync(store, docs.ToArray());

        var query = new double[dims];
        for (var d = 0; d < dims; d++)
        {
            query[d] = rng.NextDouble() * 2 - 1;
        }

        var queryArr = new JsonArray();
        foreach (var x in query)
        {
            queryArr.Add(x);
        }

        // Brute-force expected top-k by cosine similarity descending.
        var expected = vectors
            .Select(t => (t.Id, Sim: Cosine(t.Vec, query)))
            .OrderByDescending(t => t.Sim)
            .Take(k)
            .Select(t => t.Id)
            .ToHashSet();

        var parameters = new Dictionary<string, object?> { ["@q"] = queryArr };
        var result = await engine.ExecuteQueryAsync("db", "coll",
            $"SELECT TOP {k} c.id FROM c ORDER BY VectorDistance(c.embedding, @q)",
            parameters);

        result.Resources.Should().HaveCount(k);
        var actual = result.Resources.Select(r => r["id"]!.GetValue<string>()).ToHashSet();
        var recall = actual.Intersect(expected).Count() / (double)k;
        recall.Should().BeGreaterThanOrEqualTo(0.8, "HNSW ANN should closely match brute-force top-k");
    }

    [Fact]
    public async Task OrderByVectorDistance_Descending_ReturnsFarthestFirst()
    {
        var (store, engine, _) = CreateSut("cosine");
        await SeedAsync(store,
            ("near", new JsonArray(1, 0, 0)),
            ("far", new JsonArray(0, 1, 0)),
            ("mid", new JsonArray(0.9, 0.1, 0)));

        // DESC == farthest first (Azure convention).
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 3 c.id FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0]) DESC");

        result.Resources.Should().HaveCount(3);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("far");
        result.Resources[2]["id"]!.GetValue<string>().Should().Be("near");
    }

    [Fact]
    public async Task VectorDistance_BooleanBruteForceArg_ReturnsNearestFirst()
    {
        var (store, engine, _) = CreateSut("cosine");
        await SeedAsync(store,
            ("near", new JsonArray(1, 0, 0)),
            ("far", new JsonArray(0, 1, 0)),
            ("mid", new JsonArray(0.9, 0.1, 0)));

        // The boolean 3rd argument (true) forces an exhaustive scan; results must
        // still be ordered nearest-first.
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 3 c.id FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0], true)");

        result.Resources.Should().HaveCount(3);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("near");
        result.Resources[2]["id"]!.GetValue<string>().Should().Be("far");
    }

    [Fact]
    public async Task PartitionScopedQuery_UsesGraph_ReturnsOnlyThatPartitionNearest()
    {
        const int dims = 16;
        var rng = new Random(7);
        // Threshold below the per-partition count forces the partition-filtered graph
        // path (rather than a per-partition exact scan). Synchronous build for determinism.
        var options = new VectorIndexOptions { BackgroundBuild = false, PartitionExactScanThreshold = 4 };
        var (store, engine, _) = CreateSut("cosine", dimensions: dims, options: options);

        JsonArray RandomVec()
        {
            var arr = new JsonArray();
            for (var d = 0; d < dims; d++)
            {
                arr.Add(rng.NextDouble() * 2 - 1);
            }

            return arr;
        }

        JsonArray AxisVec()
        {
            var arr = new JsonArray();
            for (var d = 0; d < dims; d++)
            {
                arr.Add(d == 0 ? 1.0 : 0.0);
            }

            return arr;
        }

        // t1 holds the exact match plus filler; t2 holds only filler.
        await store.CreateDocumentAsync("db", "coll", NewDocPk("t1-hit", "t1", AxisVec()));
        for (var i = 0; i < 20; i++)
        {
            await store.CreateDocumentAsync("db", "coll", NewDocPk($"t1-{i}", "t1", RandomVec()));
            await store.CreateDocumentAsync("db", "coll", NewDocPk($"t2-{i}", "t2", RandomVec()));
        }

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 5 c.id FROM c ORDER BY VectorDistance(c.embedding, " +
            "[1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0])",
            parameters: null,
            options: new QueryOptions { PartitionKey = PartitionKeyValue.Create("t1") });

        result.Resources.Should().NotBeEmpty();
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("t1-hit");
        result.Resources.Select(r => r["id"]!.GetValue<string>())
            .Should().OnlyContain(id => id.StartsWith("t1"), "partition-scoped search must not return other partitions");
    }

    private static double Cosine(double[] a, double[] b)
    {
        double dot = 0, ma = 0, mb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            ma += a[i] * a[i];
            mb += b[i] * b[i];
        }

        var mag = Math.Sqrt(ma) * Math.Sqrt(mb);
        return mag == 0 ? 0 : dot / mag;
    }

    private static async Task SeedAsync(IDocumentStore store, params (string Id, JsonArray? Embedding)[] docs)
    {
        foreach (var (id, embedding) in docs)
        {
            await store.CreateDocumentAsync("db", "coll", NewDoc(id, embedding));
        }
    }

    private static JsonObject NewDoc(string id, JsonArray? embedding)
    {
        var doc = new JsonObject { ["id"] = id, ["tenantId"] = "t1" };
        if (embedding is not null)
        {
            doc["embedding"] = embedding;
        }

        return doc;
    }

    private static JsonObject NewDocPk(string id, string tenantId, JsonArray embedding) =>
        new() { ["id"] = id, ["tenantId"] = tenantId, ["embedding"] = embedding };

    private static (IDocumentStore Store, CosmosQueryEngine Engine, IVectorIndexProvider Provider) CreateSut(
        string distanceFunction, int dimensions = 3, VectorIndexOptions? options = null)
    {
        var inner = new SurrealDbDocumentStore(
            new SurrealDbConnectionManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            new InMemoryChangeFeedProvider());
        var provider = new HnswVectorIndexProvider(inner, options ?? new VectorIndexOptions());
        var store = new VectorIndexingDocumentStore(inner, provider);

        store.CreateDatabaseAsync("db").GetAwaiter().GetResult();
        store.CreateContainerAsync("db", new CosmosContainer
        {
            Id = "coll",
            DatabaseId = "db",
            PartitionKey = new PartitionKeyDefinition { Paths = ["/tenantId"] },
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy
            {
                VectorEmbeddings =
                [
                    new VectorEmbedding
                    {
                        Path = "/embedding",
                        DataType = "float32",
                        DistanceFunction = distanceFunction,
                        Dimensions = dimensions
                    }
                ]
            },
            IndexingPolicy = new IndexingPolicy
            {
                VectorIndexes = [new VectorIndex { Path = "/embedding", Type = "diskANN" }]
            }
        }).GetAwaiter().GetResult();

        var engine = new CosmosQueryEngine(inner, new IndexValidationService(), vectorIndexProvider: provider);
        return (store, engine, provider);
    }
}
