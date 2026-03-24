using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class IndexingEnforcementTests
{
    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<(string DatabaseId, string ContainerId, string PartitionKey)> CreateContainerAsync(
        TestServerFixture fixture,
        object? indexingPolicy = null)
    {
        var dbId = NewId("db");
        var containerId = NewId("coll");
        const string partitionKey = "tenant-a";

        using (var dbRequest = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbResponse = await fixture.Client.SendAsync(dbRequest))
        {
            dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var containerBody = new Dictionary<string, object?>
        {
            ["id"] = containerId,
            ["partitionKey"] = new { paths = new[] { "/tenantId" }, kind = "Hash", version = 2 }
        };

        if (indexingPolicy is not null)
        {
            containerBody["indexingPolicy"] = indexingPolicy;
        }

        using (var containerRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", containerBody))
        using (var containerResponse = await fixture.Client.SendAsync(containerRequest))
        {
            containerResponse.StatusCode.Should().Be(HttpStatusCode.Created,
                "container creation should succeed; response: {0}",
                await containerResponse.Content.ReadAsStringAsync());
        }

        return (dbId, containerId, partitionKey);
    }

    private static async Task CreateDocumentAsync(
        TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids,
        string name,
        int age = 30)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = NewId("doc"),
            tenantId = ids.PartitionKey,
            name,
            age
        });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<HttpResponseMessage> ExecuteQueryAsync(
        TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids,
        string query,
        bool enableScan = false)
    {
        var url = $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = JsonContent.Create(new { query });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.EnableCrossPartition, "true");

        if (enableScan)
        {
            request.Headers.TryAddWithoutValidation(CosmosHeaders.EnableScan, "true");
        }

        return await fixture.Client.SendAsync(request);
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content);
        body.Should().NotBeNull();
        return body!.AsObject();
    }

    /// <summary>Gets documents array from query response (handles both PascalCase and camelCase).</summary>
    private static JsonArray GetDocuments(JsonObject body)
    {
        var docs = body["Documents"] ?? body["documents"];
        docs.Should().NotBeNull("response should contain Documents array");
        return docs!.AsArray();
    }

    [Fact]
    public async Task QueryWithIndexingModeNone_WithoutScanHeader_Returns400()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerAsync(fixture, indexingPolicy: new
        {
            indexingMode = "none",
            automatic = false,
            includedPaths = Array.Empty<object>(),
            excludedPaths = Array.Empty<object>()
        });

        await CreateDocumentAsync(fixture, ids, "Alice");

        using var response = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c WHERE c.name = 'Alice'");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadBodyAsync(response);
        body["message"]!.GetValue<string>().Should().Contain("indexing mode is set to None");
    }

    [Fact]
    public async Task QueryWithIndexingModeNone_WithScanHeader_Returns200()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerAsync(fixture, indexingPolicy: new
        {
            indexingMode = "none",
            automatic = false,
            includedPaths = Array.Empty<object>(),
            excludedPaths = Array.Empty<object>()
        });

        await CreateDocumentAsync(fixture, ids, "Alice");

        using var response = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c WHERE c.name = 'Alice'", enableScan: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        var documents = GetDocuments(body);
        documents.Should().HaveCount(1);
    }

    [Fact]
    public async Task QueryOnExcludedPath_WithoutScanHeader_Returns400()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerAsync(fixture, indexingPolicy: new
        {
            indexingMode = "consistent",
            automatic = true,
            includedPaths = new[] { new { path = "/*" } },
            excludedPaths = new[] { new { path = "/name/?" } }
        });

        await CreateDocumentAsync(fixture, ids, "Alice");

        using var response = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c WHERE c.name = 'Alice'");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadBodyAsync(response);
        body["message"]!.GetValue<string>().Should().Contain("excluded");
    }

    [Fact]
    public async Task QueryOnExcludedPath_WithScanHeader_Returns200()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerAsync(fixture, indexingPolicy: new
        {
            indexingMode = "consistent",
            automatic = true,
            includedPaths = new[] { new { path = "/*" } },
            excludedPaths = new[] { new { path = "/name/?" } }
        });

        await CreateDocumentAsync(fixture, ids, "Alice");

        using var response = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c WHERE c.name = 'Alice'", enableScan: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        var documents = GetDocuments(body);
        documents.Should().HaveCount(1);
    }

    [Fact]
    public async Task MultiPropertyOrderBy_WithoutCompositeIndex_Returns400()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerAsync(fixture);

        await CreateDocumentAsync(fixture, ids, "Alice", age: 30);
        await CreateDocumentAsync(fixture, ids, "Bob", age: 25);

        using var response = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c ORDER BY c.name ASC, c.age DESC");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadBodyAsync(response);
        body["message"]!.GetValue<string>().Should().Contain("composite index");
    }

    [Fact]
    public async Task MultiPropertyOrderBy_WithMatchingCompositeIndex_Returns200()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerAsync(fixture, indexingPolicy: new
        {
            indexingMode = "consistent",
            automatic = true,
            includedPaths = new[] { new { path = "/*" } },
            excludedPaths = new[] { new { path = "/\"_etag\"/?" } },
            compositeIndexes = new object[]
            {
                new
                {
                    paths = new[]
                    {
                        new { path = "/name", order = "ascending" },
                        new { path = "/age", order = "descending" }
                    }
                }
            }
        });

        await CreateDocumentAsync(fixture, ids, "Alice", age: 30);
        await CreateDocumentAsync(fixture, ids, "Bob", age: 25);

        using var response = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c ORDER BY c.name ASC, c.age DESC");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        var documents = GetDocuments(body);
        documents.Should().HaveCount(2);
    }

    [Fact]
    public async Task DefaultIndexingPolicy_QueriesSucceedNormally()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerAsync(fixture);

        await CreateDocumentAsync(fixture, ids, "Alice", age: 30);
        await CreateDocumentAsync(fixture, ids, "Bob", age: 25);

        using var response = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c WHERE c.name = 'Alice'");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        var documents = GetDocuments(body);
        documents.Should().HaveCount(1);
        documents[0]!["name"]!.GetValue<string>().Should().Be("Alice");
    }
}
