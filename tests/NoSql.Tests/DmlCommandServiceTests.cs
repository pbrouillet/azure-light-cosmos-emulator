using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class DmlCommandServiceTests
{
    [Fact]
    public async Task Insert_CreatesDocumentAndReturnsIt()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        using var result = await ExecuteQueryAsync(fixture, ids,
            """INSERT INTO c VALUES ({"id": "ins-1", "tenantId": "tenant-a", "name": "Alice"})""");

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        var docs = body["Documents"]!.AsArray();
        docs.Should().HaveCount(1);
        docs[0]!["id"]!.GetValue<string>().Should().Be("ins-1");
        docs[0]!["name"]!.GetValue<string>().Should().Be("Alice");
        body["_count"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Insert_WithParameter_CreatesDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var result = await ExecuteQueryAsync(fixture, ids,
            "INSERT INTO c VALUES (@doc)",
            new JsonArray(new JsonObject
            {
                ["name"] = "@doc",
                ["value"] = new JsonObject
                {
                    ["id"] = "ins-param",
                    ["tenantId"] = "tenant-a",
                    ["name"] = "Bob"
                }
            }));

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        body["Documents"]!.AsArray().Should().HaveCount(1);
        body["Documents"]![0]!["id"]!.GetValue<string>().Should().Be("ins-param");
    }

    [Fact]
    public async Task Insert_DuplicateId_ReturnsConflict()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // First insert succeeds
        var first = await ExecuteQueryAsync(fixture, ids,
            """INSERT INTO c VALUES ({"id": "dup-1", "tenantId": "tenant-a", "name": "First"})""");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second insert with same id should fail
        var second = await ExecuteQueryAsync(fixture, ids,
            """INSERT INTO c VALUES ({"id": "dup-1", "tenantId": "tenant-a", "name": "Second"})""");
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Update_ModifiesMatchingDocuments()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Seed a document
        await ExecuteQueryAsync(fixture, ids,
            """INSERT INTO c VALUES ({"id": "upd-1", "tenantId": "tenant-a", "name": "Old", "score": 10})""");

        // Update it
        var result = await ExecuteQueryAsync(fixture, ids,
            "UPDATE c SET c.name = 'New', c.score = 42 WHERE c.id = 'upd-1'");

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        var docs = body["Documents"]!.AsArray();
        docs.Should().HaveCount(1);
        docs[0]!["name"]!.GetValue<string>().Should().Be("New");
        docs[0]!["score"]!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public async Task Update_NoMatch_ReturnsEmptyResult()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var result = await ExecuteQueryAsync(fixture, ids,
            "UPDATE c SET c.name = 'Ghost' WHERE c.id = 'nonexistent'");

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        body["Documents"]!.AsArray().Should().BeEmpty();
        body["_count"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Delete_RemovesMatchingDocuments()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Seed documents
        await ExecuteQueryAsync(fixture, ids,
            """INSERT INTO c VALUES ({"id": "del-1", "tenantId": "tenant-a", "name": "ToDelete"})""");
        await ExecuteQueryAsync(fixture, ids,
            """INSERT INTO c VALUES ({"id": "del-2", "tenantId": "tenant-a", "name": "ToKeep"})""");

        // Delete one
        var result = await ExecuteQueryAsync(fixture, ids,
            "DELETE FROM c WHERE c.id = 'del-1'");

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        var deletedDocs = body["Documents"]!.AsArray();
        deletedDocs.Should().HaveCount(1);
        deletedDocs[0]!["id"]!.GetValue<string>().Should().Be("del-1");

        // Verify the other document still exists
        var remaining = await ExecuteQueryAsync(fixture, ids, "SELECT * FROM c");
        var remainingBody = await ReadBodyAsync(remaining);
        remainingBody["Documents"]!.AsArray().Should().HaveCount(1);
        remainingBody["Documents"]![0]!["id"]!.GetValue<string>().Should().Be("del-2");
    }

    [Fact]
    public async Task Delete_NoMatch_ReturnsEmptyResult()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var result = await ExecuteQueryAsync(fixture, ids,
            "DELETE FROM c WHERE c.id = 'nonexistent'");

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        body["Documents"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Insert_WithComments_Works()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var result = await ExecuteQueryAsync(fixture, ids,
            """
            -- Insert a new document
            INSERT INTO c VALUES ({"id": "cmt-1", "tenantId": "tenant-a", "name": "Commented"})
            """);

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        body["Documents"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Update_SetNestedField_Works()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        await ExecuteQueryAsync(fixture, ids,
            """INSERT INTO c VALUES ({"id": "nest-1", "tenantId": "tenant-a", "address": {"city": "Seattle"}})""");

        var result = await ExecuteQueryAsync(fixture, ids,
            "UPDATE c SET c.address.city = 'Portland' WHERE c.id = 'nest-1'");

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(result);
        body["Documents"]![0]!["address"]!["city"]!.GetValue<string>().Should().Be("Portland");
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task<HttpResponseMessage> ExecuteQueryAsync(
        TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids,
        string query,
        JsonArray? parameters = null)
    {
        using var request = fixture.CreateRequest(
            HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new
            {
                query,
                parameters = parameters ?? new JsonArray()
            });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.EnableCrossPartition, "true");

        return await fixture.Client.SendAsync(request);
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

    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(content);
        body.Should().NotBeNull();
        return body!.AsObject();
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
