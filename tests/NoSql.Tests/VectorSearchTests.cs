using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class VectorSearchTests
{
    [Fact]
    public async Task Container_WithVectorEmbeddingPolicy_RoundTrips()
    {
        var store = CreateStore();
        await store.CreateDatabaseAsync("db");
        var container = new CosmosContainer
        {
            Id = "vec-coll",
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
                        DistanceFunction = "cosine",
                        Dimensions = 3
                    }
                ]
            },
            IndexingPolicy = new IndexingPolicy
            {
                VectorIndexes = [new VectorIndex { Path = "/embedding", Type = "flat" }]
            }
        };

        await store.CreateContainerAsync("db", container);
        var retrieved = await store.GetContainerAsync("db", "vec-coll");

        retrieved.VectorEmbeddingPolicy.Should().NotBeNull();
        retrieved.VectorEmbeddingPolicy!.VectorEmbeddings.Should().HaveCount(1);
        retrieved.VectorEmbeddingPolicy.VectorEmbeddings[0].Path.Should().Be("/embedding");
        retrieved.VectorEmbeddingPolicy.VectorEmbeddings[0].DistanceFunction.Should().Be("cosine");
        retrieved.VectorEmbeddingPolicy.VectorEmbeddings[0].Dimensions.Should().Be(3);
        retrieved.IndexingPolicy.VectorIndexes.Should().HaveCount(1);
        retrieved.IndexingPolicy.VectorIndexes![0].Type.Should().Be("flat");
    }

    [Fact]
    public async Task VectorDistance_CosineSimilarity_IdenticalVectors()
    {
        var (store, engine) = CreateSut(distanceFunction: "cosine");
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["embedding"] = new JsonArray(1, 0, 0)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id, VectorDistance(c.embedding, [1, 0, 0]) AS score FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["score"]!.GetValue<double>().Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public async Task VectorDistance_CosineSimilarity_OrthogonalVectors()
    {
        var (store, engine) = CreateSut(distanceFunction: "cosine");
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["embedding"] = new JsonArray(1, 0, 0)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id, VectorDistance(c.embedding, [0, 1, 0]) AS score FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["score"]!.GetValue<double>().Should().BeApproximately(0.0, 0.0001);
    }

    [Fact]
    public async Task VectorDistance_DotProduct()
    {
        var (store, engine) = CreateSut(distanceFunction: "dotproduct");
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["embedding"] = new JsonArray(1, 2, 3)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VectorDistance(c.embedding, [4, 5, 6]) AS score FROM c");

        // dot product: 1*4 + 2*5 + 3*6 = 32
        result.Resources.Should().HaveCount(1);
        result.Resources[0]["score"]!.GetValue<double>().Should().BeApproximately(32.0, 0.0001);
    }

    [Fact]
    public async Task VectorDistance_EuclideanDistance()
    {
        var (store, engine) = CreateSut(distanceFunction: "euclidean");
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["embedding"] = new JsonArray(1, 0, 0)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VectorDistance(c.embedding, [0, 0, 0]) AS score FROM c");

        // euclidean: sqrt(1) = 1.0
        result.Resources.Should().HaveCount(1);
        result.Resources[0]["score"]!.GetValue<double>().Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public async Task VectorDistance_OrderBy_TopN()
    {
        var (store, engine) = CreateSut(distanceFunction: "cosine");
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["embedding"] = new JsonArray(1, 0, 0)),
            CreateDocument("doc-2", "t1", d => d["embedding"] = new JsonArray(0, 1, 0)),
            CreateDocument("doc-3", "t1", d => d["embedding"] = new JsonArray(0.9, 0.1, 0)));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT TOP 2 c.id, VectorDistance(c.embedding, [1, 0, 0]) AS score FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0]) DESC");

        result.Resources.Should().HaveCount(2);
        // Most similar first (highest cosine similarity)
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
        result.Resources[1]["id"]!.GetValue<string>().Should().Be("doc-3");
    }

    [Fact]
    public async Task VectorDistance_WithParameterizedVector()
    {
        var (store, engine) = CreateSut(distanceFunction: "cosine");
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["embedding"] = new JsonArray(1, 0, 0)));

        var parameters = new Dictionary<string, object?>
        {
            ["@queryVector"] = new JsonArray(1, 0, 0)
        };

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VectorDistance(c.embedding, @queryVector) AS score FROM c",
            parameters);

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["score"]!.GetValue<double>().Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public async Task ArrayLiteral_InSelect()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1"));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT [1, 2, 3] AS arr FROM c");

        result.Resources.Should().HaveCount(1);
        var arr = result.Resources[0]["arr"]!.AsArray();
        arr.Should().HaveCount(3);
        arr[0]!.GetValue<double>().Should().Be(1);
        arr[2]!.GetValue<double>().Should().Be(3);
    }

    [Fact]
    public async Task ArrayLiteral_EmptyArray()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1"));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT [] AS arr FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["arr"]!.AsArray().Should().HaveCount(0);
    }

    [Fact]
    public async Task VectorDistance_MissingVector_ReturnsUndefined()
    {
        var (store, engine) = CreateSut(distanceFunction: "cosine");
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1")); // no embedding field

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id, VectorDistance(c.embedding, [1, 0, 0]) AS score FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
        // VectorDistance returns undefined when vector is missing → omitted from projection
        result.Resources[0].ContainsKey("score").Should().BeFalse();
    }

    [Fact]
    public async Task VectorDistance_MultipleVectorIndexTypes_Stored()
    {
        var store = CreateStore();
        await store.CreateDatabaseAsync("db");
        var container = new CosmosContainer
        {
            Id = "multi-vec",
            DatabaseId = "db",
            PartitionKey = new PartitionKeyDefinition { Paths = ["/pk"] },
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy
            {
                VectorEmbeddings =
                [
                    new VectorEmbedding { Path = "/vec1", DataType = "float32", DistanceFunction = "cosine", Dimensions = 128 },
                    new VectorEmbedding { Path = "/vec2", DataType = "float16", DistanceFunction = "dotproduct", Dimensions = 64 }
                ]
            },
            IndexingPolicy = new IndexingPolicy
            {
                VectorIndexes =
                [
                    new VectorIndex { Path = "/vec1", Type = "diskANN" },
                    new VectorIndex { Path = "/vec2", Type = "quantizedFlat" }
                ]
            }
        };

        await store.CreateContainerAsync("db", container);
        var retrieved = await store.GetContainerAsync("db", "multi-vec");

        retrieved.VectorEmbeddingPolicy!.VectorEmbeddings.Should().HaveCount(2);
        retrieved.IndexingPolicy.VectorIndexes.Should().HaveCount(2);
        retrieved.IndexingPolicy.VectorIndexes![0].Type.Should().Be("diskANN");
        retrieved.IndexingPolicy.VectorIndexes![1].Type.Should().Be("quantizedFlat");
    }

    private static async Task SeedDocumentsAsync(IDocumentStore store, params JsonObject[] documents)
    {
        foreach (var document in documents)
        {
            await store.CreateDocumentAsync("db", "coll", document);
        }
    }

    private static JsonObject CreateDocument(string id, string tenantId, Action<JsonObject>? configure = null)
    {
        var document = new JsonObject
        {
            ["id"] = id,
            ["tenantId"] = tenantId
        };

        configure?.Invoke(document);
        return document;
    }

    private static IDocumentStore CreateStore()
    {
        return new SurrealDbDocumentStore(
            new SurrealDbConnectionManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            new InMemoryChangeFeedProvider());
    }

    private static (IDocumentStore Store, CosmosQueryEngine Engine) CreateSut(string? distanceFunction = null)
    {
        var store = CreateStore();

        store.CreateDatabaseAsync("db").GetAwaiter().GetResult();

        var vectorPolicy = distanceFunction is not null
            ? new VectorEmbeddingPolicy
            {
                VectorEmbeddings =
                [
                    new VectorEmbedding
                    {
                        Path = "/embedding",
                        DataType = "float32",
                        DistanceFunction = distanceFunction,
                        Dimensions = 3
                    }
                ]
            }
            : null;

        store.CreateContainerAsync("db", new CosmosContainer
        {
            Id = "coll",
            DatabaseId = "db",
            PartitionKey = new PartitionKeyDefinition { Paths = ["/tenantId"] },
            VectorEmbeddingPolicy = vectorPolicy,
            IndexingPolicy = distanceFunction is not null
                ? new IndexingPolicy
                {
                    VectorIndexes = [new VectorIndex { Path = "/embedding", Type = "flat" }]
                }
                : new IndexingPolicy()
        }).GetAwaiter().GetResult();

        return (store, new CosmosQueryEngine(store, new IndexValidationService()));
    }
}
