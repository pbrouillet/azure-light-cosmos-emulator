using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class QueryEngineAdvancedTests
{
    [Fact]
    public async Task ExecuteQueryAsync_SupportsOrOperator()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(
            store,
            CreateDocument("doc-1", "tenant-1", document =>
            {
                document["a"] = 1;
                document["b"] = 0;
            }),
            CreateDocument("doc-2", "tenant-1", document =>
            {
                document["a"] = 0;
                document["b"] = 2;
            }),
            CreateDocument("doc-3", "tenant-1", document =>
            {
                document["a"] = 0;
                document["b"] = 0;
            }));

        var result = await engine.ExecuteQueryAsync("db", "coll", "SELECT * FROM c WHERE c.a = 1 OR c.b = 2");

        result.Resources.Select(resource => resource["id"]!.GetValue<string>())
            .Should().BeEquivalentTo(["doc-1", "doc-2"]);
    }

    [Fact]
    public async Task ExecuteQueryAsync_SupportsNotEqualAndNotPrefix()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(
            store,
            CreateDocument("doc-1", "tenant-1", document =>
            {
                document["a"] = "x";
                document["name"] = "allowed";
            }),
            CreateDocument("doc-2", "tenant-1", document =>
            {
                document["a"] = "y";
                document["name"] = "allowed";
            }),
            CreateDocument("doc-3", "tenant-1", document =>
            {
                document["a"] = "z";
                document["name"] = "blocked item";
            }));

        var result = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT * FROM c WHERE c.a != 'x' AND NOT CONTAINS(c.name, 'blocked')");

        result.Resources.Should().ContainSingle();
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-2");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SupportsAggregatesWithoutGroupBy()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(
            store,
            CreateDocument("doc-1", "tenant-1", document => document["age"] = 10),
            CreateDocument("doc-2", "tenant-1", document => document["age"] = 20),
            CreateDocument("doc-3", "tenant-1", document => document["age"] = 30));

        var result = await engine.ExecuteQueryAsync("db", "coll", "SELECT COUNT(1), SUM(c.age) FROM c");

        result.Resources.Should().ContainSingle();
        result.Resources[0]["$1"]!.GetValue<int>().Should().Be(3);
        result.Resources[0]["$2"]!.GetValue<double>().Should().Be(60);
    }

    [Fact]
    public async Task ExecuteQueryAsync_SupportsGroupByAggregates()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(
            store,
            CreateDocument("doc-1", "tenant-1", document => document["category"] = "a"),
            CreateDocument("doc-2", "tenant-1", document => document["category"] = "a"),
            CreateDocument("doc-3", "tenant-1", document => document["category"] = "b"));

        var result = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT c.category, COUNT(1) FROM c GROUP BY c.category");

        result.Resources.Should().HaveCount(2);
        var grouped = result.Resources.ToDictionary(
            resource => resource["category"]!.GetValue<string>(),
            resource => resource["$2"]!.GetValue<int>());
        grouped.Should().Equal(new Dictionary<string, int>
        {
            ["a"] = 2,
            ["b"] = 1
        });
    }

    [Fact]
    public async Task ExecuteQueryAsync_SupportsBuiltInFunctionsInSelectAndWhere()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(
            store,
            CreateDocument("doc-1", "tenant-1", document => document["name"] = "Alice"),
            CreateDocument("doc-2", "tenant-1", document => document["name"] = "bob"));

        var selectResult = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT UPPER(c.name) FROM c WHERE c.id = 'doc-1'");

        selectResult.Resources.Should().ContainSingle();
        selectResult.Resources[0]["$1"]!.GetValue<string>().Should().Be("ALICE");

        var whereResult = await engine.ExecuteQueryAsync(
            "db",
            "coll",
            "SELECT * FROM c WHERE STARTSWITH(c.name, 'A')");

        whereResult.Resources.Should().ContainSingle();
        whereResult.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SupportsIntraDocumentJoin()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(
            store,
            CreateDocument("doc-1", "tenant-1", document =>
            {
                document["tags"] = new JsonArray("red", "blue");
            }),
            CreateDocument("doc-2", "tenant-1", document =>
            {
                document["tags"] = new JsonArray("green");
            }),
            CreateDocument("doc-3", "tenant-1", document =>
            {
                document["tags"] = new JsonArray();
            }));

        var result = await engine.ExecuteQueryAsync("db", "coll", "SELECT c.id, t FROM c JOIN t IN c.tags");

        result.Resources.Select(resource =>
                (Id: resource["id"]!.GetValue<string>(), Tag: resource["t"]!.GetValue<string>()))
            .Should().Equal(
                ("doc-1", "red"),
                ("doc-1", "blue"),
                ("doc-2", "green"));
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
