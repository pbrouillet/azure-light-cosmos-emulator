using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class EmptyContainerTests
{
    [Fact]
    public async Task DeleteAll_RemovesAllDocuments_Returns204()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var (dbId, collId) = await CreateContainerAsync(fixture);

        await CreateDocumentAsync(fixture, dbId, collId, "tenant-a", "doc1");
        await CreateDocumentAsync(fixture, dbId, collId, "tenant-b", "doc2");
        await CreateDocumentAsync(fixture, dbId, collId, "tenant-c", "doc3");

        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}/docs");
        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        deleteResponse.Headers.TryGetValues("x-ms-item-count", out var itemCountValues).Should().BeTrue();
        itemCountValues!.First().Should().Be("3");

        // Verify container is empty
        using var queryRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs",
            new { query = "SELECT * FROM c", parameters = Array.Empty<object>() });
        queryRequest.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        queryRequest.Headers.TryAddWithoutValidation(CosmosHeaders.EnableCrossPartition, "true");
        using var queryResponse = await fixture.Client.SendAsync(queryRequest);

        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await queryResponse.Content.ReadAsStringAsync())!.AsObject();
        body["_count"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task DeleteAll_EmptyContainer_Returns204WithZeroCount()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var (dbId, collId) = await CreateContainerAsync(fixture);

        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}/docs");
        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        deleteResponse.Headers.TryGetValues("x-ms-item-count", out var itemCountValues).Should().BeTrue();
        itemCountValues!.First().Should().Be("0");
    }

    [Fact]
    public async Task DeleteAll_NonExistentContainer_Returns404()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");

        using var dbRequest = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId });
        using var dbResponse = await fixture.Client.SendAsync(dbRequest);
        dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/nonexistent/docs");
        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<(string DbId, string CollId)> CreateContainerAsync(TestServerFixture fixture)
    {
        var dbId = NewId("db");
        var collId = NewId("coll");

        using (var dbRequest = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbResponse = await fixture.Client.SendAsync(dbRequest))
        {
            dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using (var containerRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
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

        return (dbId, collId);
    }

    private static async Task CreateDocumentAsync(TestServerFixture fixture, string dbId, string collId, string tenantId, string name)
    {
        var docId = NewId("doc");
        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            id = docId,
            tenantId,
            name
        });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
