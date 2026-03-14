using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class DocumentTests
{
    [Fact]
    public async Task CreateDocument_ReturnsCreatedDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = NewId("doc");

        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "created"
        });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadBodyAsync(response);
        body["id"]!.GetValue<string>().Should().Be(docId);
        body["tenantId"]!.GetValue<string>().Should().Be(ids.PartitionKey);
    }

    [Fact]
    public async Task ReadDocument_WithPartitionKeyHeader_ReturnsDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "readable");

        using var request = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["id"]!.GetValue<string>().Should().Be(docId);
        body["name"]!.GetValue<string>().Should().Be("readable");
    }

    [Fact]
    public async Task ReplaceDocument_WithMatchingEtag_UpdatesDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "before");
        var etag = await ReadEtagAsync(fixture, ids, docId);

        using var request = fixture.CreateRequest(HttpMethod.Put, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "after"
        });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IfMatch, etag);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["name"]!.GetValue<string>().Should().Be("after");
        body["_etag"]!.GetValue<string>().Should().NotBe(etag);
    }

    [Fact]
    public async Task UpsertDocument_ReplacesExistingDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "original");

        using (var upsertRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "upserted"
        }))
        {
            upsertRequest.Headers.TryAddWithoutValidation(CosmosHeaders.IsUpsert, "true");

            using var upsertResponse = await fixture.Client.SendAsync(upsertRequest);
            upsertResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var readRequest = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        readRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());

        using var readResponse = await fixture.Client.SendAsync(readRequest);

        readResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(readResponse);
        body["name"]!.GetValue<string>().Should().Be("upserted");
    }

    [Fact]
    public async Task DeleteDocument_RemovesDocument()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "delete-me");

        using (var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}"))
        {
            deleteRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());

            using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var readRequest = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        readRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());

        using var readResponse = await fixture.Client.SendAsync(readRequest);
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReplaceDocument_WithStaleEtag_ReturnsPreconditionFailed()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "before");

        using var request = fixture.CreateRequest(HttpMethod.Put, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "after"
        });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IfMatch, "\"stale-etag\"");

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        var body = await ReadBodyAsync(response);
        body["code"]!.GetValue<string>().Should().Be("PreconditionFailed");
    }

    [Fact]
    public async Task CreateDuplicateDocument_ReturnsConflict()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = NewId("doc");

        using (var firstRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "first"
        }))
        using (var firstResponse = await fixture.Client.SendAsync(firstRequest))
        {
            firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var duplicateRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name = "duplicate"
        });
        using var duplicateResponse = await fixture.Client.SendAsync(duplicateRequest);

        duplicateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await ReadBodyAsync(duplicateResponse);
        body["code"]!.GetValue<string>().Should().Be("Conflict");
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

    private static async Task<string> CreateDocumentAsync(TestServerFixture fixture, (string DatabaseId, string ContainerId, string PartitionKey) ids, string name)
    {
        var docId = NewId("doc");
        using var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = docId,
            tenantId = ids.PartitionKey,
            name
        });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return docId;
    }

    private static async Task<string> ReadEtagAsync(TestServerFixture fixture, (string DatabaseId, string ContainerId, string PartitionKey) ids, string docId)
    {
        using var request = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());

        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        return response.Headers.ETag!.Tag;
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
