using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class CosmosQueryEngineTests
{
    [Fact]
    public async Task ExecuteQueryAsync_FiltersByParameterAndPartitionKey()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "tenant-1", "alpha", 5, 2, "a", ["tag1"], true),
            CreateDocument("doc-1-alt", "tenant-2", "beta", 8, 3, "b", ["tag2"], true));

        var result = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT * FROM c WHERE c.id = @id",
            new Dictionary<string, object?> { ["@id"] = "doc-1" },
            new QueryOptions { PartitionKey = PartitionKeyValue.Create("tenant-1") });

        result.Resources.Should().ContainSingle();
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
        result.Resources[0]["tenantId"]!.GetValue<string>().Should().Be("tenant-1");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ProjectsFieldsWithTopAndOrdering()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "tenant-1", "alpha", 6, 5, "a", ["tag1"], true),
            CreateDocument("doc-2", "tenant-1", "beta", 12, 3, "a", ["tag2"], true),
            CreateDocument("doc-3", "tenant-1", "gamma", 8, 2, "a", ["tag3"], true));

        var result = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT TOP 2 c.id, c.name FROM c WHERE c.score > 5 AND c.rank < 5 ORDER BY c.score DESC");

        result.Resources.Select(resource => resource["id"]!.GetValue<string>())
            .Should().Equal("doc-2", "doc-3");
        result.Resources.All(resource => resource.Count == 2).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteQueryAsync_SupportsValueProjectionAndFunctions()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "tenant-1", "beta test", 10, 1, "a", ["tag1", "tag2"], true),
            CreateDocument("doc-2", "tenant-1", "beta sample", 10, 1, "a", ["tag2"], true),
            CreateDocument("doc-3", "tenant-1", "other test", 10, 1, "c", ["tag1"], false));

        var result = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT VALUE c.name FROM c WHERE c.category IN ('a', 'b') AND CONTAINS(c.name, 'test') AND ARRAY_CONTAINS(c.tags, 'tag1') AND IS_DEFINED(c.optional)");

        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<string>().Should().Be("beta test");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SupportsCountAndPagedOffsetLimit()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "tenant-1", "one", 1, 1, "a", ["tag1"], true),
            CreateDocument("doc-2", "tenant-1", "two", 2, 2, "a", ["tag1"], true),
            CreateDocument("doc-3", "tenant-1", "three", 3, 3, "a", ["tag1"], true),
            CreateDocument("doc-4", "tenant-1", "four", 4, 4, "b", ["tag1"], true));

        var count = await engine.ExecuteQueryAsync("db", "coll", "SELECT COUNT(1) FROM c WHERE c.category = 'a'");
        count.Resources.Should().ContainSingle();
        count.Resources[0]["$1"]!.GetValue<int>().Should().Be(3);

        var firstPage = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT * FROM c ORDER BY c.rank ASC OFFSET @offset LIMIT @limit",
            new Dictionary<string, object?>
            {
                ["@offset"] = 1,
                ["@limit"] = 2
            },
            new QueryOptions { MaxItemCount = 1 });

        firstPage.Resources.Should().ContainSingle();
        firstPage.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-2");
        firstPage.ContinuationToken.Should().Be("1");

        var secondPage = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT * FROM c ORDER BY c.rank ASC OFFSET @offset LIMIT @limit",
            new Dictionary<string, object?>
            {
                ["@offset"] = 1,
                ["@limit"] = 2
            },
            new QueryOptions { MaxItemCount = 1, ContinuationToken = firstPage.ContinuationToken });

        secondPage.Resources.Should().ContainSingle();
        secondPage.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-3");
        secondPage.ContinuationToken.Should().BeNull();
    }

    private static async Task SeedDocumentsAsync(IDocumentStore store, params JsonObject[] documents)
    {
        foreach (var document in documents)
        {
            await store.CreateDocumentAsync("db", "coll", document);
        }
    }

    private static JsonObject CreateDocument(string id, string tenantId, string name, int score, int rank, string category, string[] tags, bool includeOptional)
    {
        var document = new JsonObject
        {
            ["id"] = id,
            ["tenantId"] = tenantId,
            ["name"] = name,
            ["score"] = score,
            ["rank"] = rank,
            ["category"] = category,
            ["tags"] = new JsonArray(tags.Select(tag => (JsonNode?)tag).ToArray())
        };

        if (includeOptional)
        {
            document["optional"] = true;
        }

        return document;
    }

    private static (IDocumentStore Store, CosmosQueryEngine Engine) CreateSut()
    {
        var store = new SurrealDbDocumentStore(
            new SurrealDbConnectionManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            new InMemoryChangeFeedProvider());

        store.CreateDatabaseAsync("db").GetAwaiter().GetResult();
        store.CreateContainerAsync("db", new CosmosContainer
        {
            Id = "coll",
            DatabaseId = "db",
            PartitionKey = new PartitionKeyDefinition
            {
                Paths = ["/tenantId"]
            }
        }).GetAwaiter().GetResult();

        return (store, new CosmosQueryEngine(store, new IndexValidationService()));
    }
}
