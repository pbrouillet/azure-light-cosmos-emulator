using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class IndexingDirectiveTests
{
    [Fact]
    public async Task CreateWithExclude_DocNotInQueryResults_ButReadableById()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = NewId("doc");

        // Create with Exclude directive
        using (var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "excluded"
        }))
        {
            request.Headers.TryAddWithoutValidation(CosmosHeaders.IndexingDirective, "Exclude");
            using var response = await fixture.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Query should return 0 results
        var queryResult = await QueryAsync(fixture, ids, "SELECT * FROM c");
        queryResult["_count"]!.GetValue<int>().Should().Be(0);

        // Read by ID should still work
        using var readRequest = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        readRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        using var readResponse = await fixture.Client.SendAsync(readRequest);
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(readResponse);
        body["id"]!.GetValue<string>().Should().Be(docId);
    }

    [Fact]
    public async Task CreateWithInclude_DocAppearsInQueryResults()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = NewId("doc");

        using (var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "included"
        }))
        {
            request.Headers.TryAddWithoutValidation(CosmosHeaders.IndexingDirective, "Include");
            using var response = await fixture.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var queryResult = await QueryAsync(fixture, ids, "SELECT * FROM c");
        queryResult["_count"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task CreateWithoutDirective_DocAppearsInQueryResults()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = NewId("doc");

        using (var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "default"
        }))
        {
            using var response = await fixture.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var queryResult = await QueryAsync(fixture, ids, "SELECT * FROM c");
        queryResult["_count"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ReplaceWithExclude_DocDisappearsFromQueryResults()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = NewId("doc");

        // Create normally
        using (var createRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "visible"
        }))
        {
            using var createResponse = await fixture.Client.SendAsync(createRequest);
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Verify it appears in query
        var beforeQuery = await QueryAsync(fixture, ids, "SELECT * FROM c");
        beforeQuery["_count"]!.GetValue<int>().Should().Be(1);

        // Replace with Exclude
        using (var replaceRequest = fixture.CreateRequest(HttpMethod.Put, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "now-excluded"
        }))
        {
            replaceRequest.Headers.TryAddWithoutValidation(CosmosHeaders.IndexingDirective, "Exclude");
            using var replaceResponse = await fixture.Client.SendAsync(replaceRequest);
            replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Query should return 0 results
        var afterQuery = await QueryAsync(fixture, ids, "SELECT * FROM c");
        afterQuery["_count"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task ReplaceWithInclude_DocReappearsInQueryResults()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = NewId("doc");

        // Create with Exclude
        using (var createRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "excluded"
        }))
        {
            createRequest.Headers.TryAddWithoutValidation(CosmosHeaders.IndexingDirective, "Exclude");
            using var createResponse = await fixture.Client.SendAsync(createRequest);
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Verify not in query
        var beforeQuery = await QueryAsync(fixture, ids, "SELECT * FROM c");
        beforeQuery["_count"]!.GetValue<int>().Should().Be(0);

        // Replace with Include
        using (var replaceRequest = fixture.CreateRequest(HttpMethod.Put, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "now-included"
        }))
        {
            replaceRequest.Headers.TryAddWithoutValidation(CosmosHeaders.IndexingDirective, "Include");
            using var replaceResponse = await fixture.Client.SendAsync(replaceRequest);
            replaceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Query should now return 1 result
        var afterQuery = await QueryAsync(fixture, ids, "SELECT * FROM c");
        afterQuery["_count"]!.GetValue<int>().Should().Be(1);
    }

    private static async Task<(string DatabaseId, string ContainerId, string PartitionKey)> CreateContainerHierarchyAsync(TestServerFixture fixture)
    {
        var dbId = NewId("db");
        var containerId = NewId("coll");
        const string partitionKey = "tenant-a";

        using (var dbRequest = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbResponse = await fixture.Client.SendAsync(dbRequest))
        {
            dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using (var containerRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = containerId,
            partitionKey = new
            {
                paths = new[] { "/tenantId" },
                kind = "Hash",
                version = 2
            }
        }))
        using (var containerResponse = await fixture.Client.SendAsync(containerRequest))
        {
            containerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return (dbId, containerId, partitionKey);
    }

    private static async Task<JsonObject> QueryAsync(TestServerFixture fixture, (string DatabaseId, string ContainerId, string PartitionKey) ids, string queryText)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            query = queryText
        });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.EnableCrossPartition, "true");
        request.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CosmosHeaders.QueryJsonContentType);

        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadBodyAsync(response);
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content);
        body.Should().NotBeNull();
        return body!.AsObject();
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
