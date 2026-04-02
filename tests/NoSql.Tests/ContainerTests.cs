using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class ContainerTests
{
    [Fact]
    public async Task CreateContainer_WithPartitionKey_ReturnsCreatedContainer()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        var collId = NewId("coll");
        await CreateDatabaseAsync(fixture, dbId);

        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new
            {
                paths = new[] { "/tenantId" },
                kind = "Hash",
                version = 2
            }
        });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadBodyAsync(response);
        body["id"]!.GetValue<string>().Should().Be(collId);
        body["partitionKey"]!["paths"]!.AsArray().Select(path => path!.GetValue<string>()).Should().BeEquivalentTo(new[] { "/tenantId" });
    }

    [Fact]
    public async Task ListContainers_ReturnsCreatedContainers()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        var collA = NewId("coll");
        var collB = NewId("coll");
        await CreateDatabaseAsync(fixture, dbId);
        await CreateContainerAsync(fixture, dbId, collA);
        await CreateContainerAsync(fixture, dbId, collB);

        using var request = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}/colls");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        var ids = body["DocumentCollections"]!
            .AsArray()
            .Select(container => container!["id"]!.GetValue<string>())
            .ToArray();

        ids.Should().BeEquivalentTo(new[] { collA, collB });
        body["_count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task DeleteContainer_RemovesContainer()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        var collId = NewId("coll");
        await CreateDatabaseAsync(fixture, dbId);
        await CreateContainerAsync(fixture, dbId, collId);

        using (var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}"))
        using (var deleteResponse = await fixture.Client.SendAsync(deleteRequest))
        {
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var getRequest = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}/colls/{collId}");
        using var getResponse = await fixture.Client.SendAsync(getRequest);

        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateContainer_InMissingDatabase_ReturnsNotFound()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        var collId = NewId("coll");

        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new
            {
                paths = new[] { "/tenantId" },
                kind = "Hash",
                version = 2
            }
        });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ReadBodyAsync(response);
        body["code"]!.GetValue<string>().Should().Be("NotFound");
    }

    private static async Task CreateDatabaseAsync(TestServerFixture fixture, string dbId)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task CreateContainerAsync(TestServerFixture fixture, string dbId, string collId)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new
            {
                paths = new[] { "/tenantId" },
                kind = "Hash",
                version = 2
            }
        });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
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
