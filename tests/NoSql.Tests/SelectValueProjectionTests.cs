using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

/// <summary>
/// Integration tests verifying that SELECT VALUE queries return unwrapped
/// scalar values in the Documents array, matching real Cosmos DB behavior.
/// </summary>
public class SelectValueProjectionTests
{
    [Fact]
    public async Task SelectValueCount_ReturnsScalar()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var (dbId, collId) = await CreateContainerWithDocsAsync(fixture);

        var body = await ExecuteQueryAsync(fixture, dbId, collId, "SELECT VALUE COUNT(1) FROM c");

        var documents = body["Documents"]!.AsArray();
        documents.Should().ContainSingle();
        // Should be a raw scalar (e.g., 3), NOT {"$1": 3}
        documents[0]!.GetValueKind().Should().Be(JsonValueKind.Number);
        documents[0]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task SelectValueField_ReturnsScalarArray()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var (dbId, collId) = await CreateContainerWithDocsAsync(fixture);

        var body = await ExecuteQueryAsync(fixture, dbId, collId, "SELECT VALUE c.name FROM c ORDER BY c.name");

        var documents = body["Documents"]!.AsArray();
        documents.Should().HaveCount(3);
        // Each element should be a raw string, NOT {"$1": "..."}
        documents[0]!.GetValueKind().Should().Be(JsonValueKind.String);
        documents[0]!.GetValue<string>().Should().Be("alice");
        documents[1]!.GetValue<string>().Should().Be("bob");
        documents[2]!.GetValue<string>().Should().Be("charlie");
    }

    [Fact]
    public async Task SelectCountWithoutValue_ReturnsWrappedObject()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var (dbId, collId) = await CreateContainerWithDocsAsync(fixture);

        var body = await ExecuteQueryAsync(fixture, dbId, collId, "SELECT COUNT(1) FROM c");

        var documents = body["Documents"]!.AsArray();
        documents.Should().ContainSingle();
        // Without VALUE, result should be an object with "$1" key
        documents[0]!.GetValueKind().Should().Be(JsonValueKind.Object);
        documents[0]!.AsObject()["$1"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task SelectValueWithNull_ReturnsNullElements()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var (dbId, collId) = await CreateContainerWithDocsAsync(fixture);

        var body = await ExecuteQueryAsync(fixture, dbId, collId,
            "SELECT VALUE c.missing FROM c");

        var documents = body["Documents"]!.AsArray();
        // Undefined fields are projected as null per current emulator behavior
        foreach (var element in documents)
        {
            element.Should().BeNull();
        }
    }

    private static async Task<JsonObject> ExecuteQueryAsync(TestServerFixture fixture, string dbId, string collId, string query)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs",
            new { query, parameters = Array.Empty<object>() });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.EnableCrossPartition, "true");

        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(json)!.AsObject();
    }

    private static async Task<(string DbId, string CollId)> CreateContainerWithDocsAsync(TestServerFixture fixture)
    {
        var dbId = $"db-{Guid.NewGuid():N}";
        var collId = $"coll-{Guid.NewGuid():N}";

        using (var r = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
            (await fixture.Client.SendAsync(r)).StatusCode.Should().Be(HttpStatusCode.Created);

        using (var r = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new { paths = new[] { "/tenantId" }, kind = "Hash", version = 2 }
        }))
            (await fixture.Client.SendAsync(r)).StatusCode.Should().Be(HttpStatusCode.Created);

        foreach (var name in new[] { "alice", "bob", "charlie" })
        {
            using var r = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
            {
                id = $"doc-{name}",
                tenantId = "t1",
                name
            });
            (await fixture.Client.SendAsync(r)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return (dbId, collId);
    }
}
