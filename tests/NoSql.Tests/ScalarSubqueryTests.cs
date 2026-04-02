using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class ScalarSubqueryTests
{
    [Fact]
    public async Task ScalarSubquery_InSelect_ReturnsCount()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["tags"] = new JsonArray("a", "b", "c")),
            CreateDocument("doc-2", "t1", d => d["tags"] = new JsonArray("x")),
            CreateDocument("doc-3", "t1", d => d["tags"] = new JsonArray()));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id, (SELECT VALUE COUNT(1) FROM t IN c.tags) AS tagCount FROM c ORDER BY c.id");

        result.Resources.Should().HaveCount(3);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
        result.Resources[0]["tagCount"]!.GetValue<double>().Should().Be(3);
        result.Resources[1]["tagCount"]!.GetValue<double>().Should().Be(1);
        result.Resources[2]["tagCount"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task ScalarSubquery_InSelect_WithSelectValue()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d =>
            {
                d["name"] = "Alice";
                d["scores"] = new JsonArray(10, 20, 30);
            }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT VALUE (SELECT VALUE COUNT(1) FROM s IN c.scores) FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["$1"]!.GetValue<double>().Should().Be(3);
    }

    [Fact]
    public async Task ScalarSubquery_InWhere_FiltersCorrectly()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["tags"] = new JsonArray("a", "b", "c")),
            CreateDocument("doc-2", "t1", d => d["tags"] = new JsonArray("x")),
            CreateDocument("doc-3", "t1", d => d["tags"] = new JsonArray("a", "b")));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id FROM c WHERE (SELECT VALUE COUNT(1) FROM t IN c.tags) > 1 ORDER BY c.id");

        result.Resources.Select(r => r["id"]!.GetValue<string>())
            .Should().BeEquivalentTo(["doc-1", "doc-3"]);
    }

    [Fact]
    public async Task ScalarSubquery_ReturningObject()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d =>
            {
                d["name"] = "Alice";
                d["tags"] = new JsonArray("a", "b");
            }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT (SELECT VALUE COUNT(1) FROM t IN c.tags) FROM c");

        result.Resources.Should().HaveCount(1);
        // Auto-aliased as $1
        result.Resources[0]["$1"]!.GetValue<double>().Should().Be(2);
    }

    [Fact]
    public async Task ScalarSubquery_EmptyResult_ReturnsUndefined()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["name"] = "Alice"));

        // Subquery that returns 0 rows → undefined → omitted from projection
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id, (SELECT VALUE c2.name FROM c2 WHERE c2.id = 'nonexistent') AS sub FROM c");

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
        // Undefined values are omitted from projections
        result.Resources[0].ContainsKey("sub").Should().BeFalse();
    }

    [Fact]
    public async Task FromSubquery_BasicProjection()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => { d["name"] = "Alice"; d["active"] = true; }),
            CreateDocument("doc-2", "t1", d => { d["name"] = "Bob"; d["active"] = false; }),
            CreateDocument("doc-3", "t1", d => { d["name"] = "Charlie"; d["active"] = true; }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT sub.id, sub.name FROM (SELECT c.id, c.name FROM c WHERE c.active = true) AS sub ORDER BY sub.id");

        result.Resources.Should().HaveCount(2);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
        result.Resources[0]["name"]!.GetValue<string>().Should().Be("Alice");
        result.Resources[1]["id"]!.GetValue<string>().Should().Be("doc-3");
        result.Resources[1]["name"]!.GetValue<string>().Should().Be("Charlie");
    }

    [Fact]
    public async Task FromSubquery_WithOuterWhere()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => { d["name"] = "Alice"; d["score"] = 90; }),
            CreateDocument("doc-2", "t1", d => { d["name"] = "Bob"; d["score"] = 50; }),
            CreateDocument("doc-3", "t1", d => { d["name"] = "Charlie"; d["score"] = 80; }));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT sub.name FROM (SELECT c.name, c.score FROM c) AS sub WHERE sub.score > 60 ORDER BY sub.name");

        result.Resources.Should().HaveCount(2);
        result.Resources[0]["name"]!.GetValue<string>().Should().Be("Alice");
        result.Resources[1]["name"]!.GetValue<string>().Should().Be("Charlie");
    }

    [Fact]
    public async Task FromSubquery_SelectStar()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d => d["name"] = "Alice"),
            CreateDocument("doc-2", "t1", d => d["name"] = "Bob"));

        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT * FROM (SELECT c.id, c.name FROM c ORDER BY c.id) AS sub");

        result.Resources.Should().HaveCount(2);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
    }

    [Fact]
    public async Task ScalarSubquery_WithParameters()
    {
        var (store, engine) = CreateSut();
        await SeedDocumentsAsync(store,
            CreateDocument("doc-1", "t1", d =>
            {
                d["name"] = "Alice";
                d["tags"] = new JsonArray("a", "b", "c");
            }));

        var parameters = new Dictionary<string, object?> { ["@minCount"] = 2.0 };
        var result = await engine.ExecuteQueryAsync("db", "coll",
            "SELECT c.id FROM c WHERE (SELECT VALUE COUNT(1) FROM t IN c.tags) > @minCount",
            parameters);

        result.Resources.Should().HaveCount(1);
        result.Resources[0]["id"]!.GetValue<string>().Should().Be("doc-1");
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
