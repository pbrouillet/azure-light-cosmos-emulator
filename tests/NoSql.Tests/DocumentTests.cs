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

    // ─────── Missing partition key → 400 ───────

    [Fact]
    public async Task DeleteDocument_WithoutPartitionKey_ReturnsBadRequest()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "to-delete");

        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        // Intentionally omit the x-ms-documentdb-partitionkey header
        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadBodyAsync(deleteResponse);
        body["code"]!.GetValue<string>().Should().Be("BadRequest");
        body["message"]!.GetValue<string>().Should().Contain("PartitionKey");
    }

    [Fact]
    public async Task ReadDocument_WithoutPartitionKey_ReturnsBadRequest()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "readable");

        using var readRequest = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        // Intentionally omit the x-ms-documentdb-partitionkey header
        using var readResponse = await fixture.Client.SendAsync(readRequest);

        readResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadBodyAsync(readResponse);
        body["code"]!.GetValue<string>().Should().Be("BadRequest");
    }

    // ─────── Partition key mismatch → 404 with diagnostic info ───────

    [Fact]
    public async Task DeleteDocument_WithWrongPartitionKey_Returns404WithDiagnostic()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "target");

        // Delete with the WRONG partition key value
        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        deleteRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, "[\"wrong-tenant\"]");

        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ReadBodyAsync(deleteResponse);
        var message = body["message"]!.GetValue<string>();
        // The diagnostic should mention both the actual PK and the searched PK
        message.Should().Contain(ids.PartitionKey);
        message.Should().Contain("wrong-tenant");
    }

    [Fact]
    public async Task DeleteDocument_WithEmptyPartitionKeyArray_ReturnsBadRequest()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        var docId = await CreateDocumentAsync(fixture, ids, "target");

        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        deleteRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, "[]");

        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await ReadBodyAsync(deleteResponse);
        body["message"]!.GetValue<string>().Should().Contain("empty");
    }

    // ─────── Reproduce: query then delete (Explorer scenario) ───────

    [Fact]
    public async Task DeleteDocument_AfterQueryingDocuments_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Create a document with a SHA-256-style hex ID (matches user scenario)
        var hexId = "fed886c04b0672b46b2c8a827023bffe81a8be89016fb09222fb216d156c1af0";
        using (var createRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = hexId,
            tenantId = ids.PartitionKey,
            name = "vector-import"
        }))
        {
            using var createResponse = await fixture.Client.SendAsync(createRequest);
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Query documents via SELECT * FROM c (cross-partition, same as Explorer)
        using var queryRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            query = "SELECT * FROM c",
            parameters = Array.Empty<object>()
        });
        queryRequest.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        queryRequest.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");
        queryRequest.Headers.TryAddWithoutValidation("x-ms-max-item-count", "50");

        using var queryResponse = await fixture.Client.SendAsync(queryRequest);
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var queryBody = await ReadBodyAsync(queryResponse);
        var documents = queryBody["Documents"]!.AsArray();
        documents.Should().ContainSingle();

        // Extract the partition key from the queried document (same as Explorer)
        var doc = documents[0]!.AsObject();
        var pkValue = doc["tenantId"]!.GetValue<string>();
        var pkHeader = $"[\"{pkValue}\"]";

        // Delete the document using the extracted PK (same as Explorer)
        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{hexId}");
        deleteRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, pkHeader);

        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─────── PK path = /id (partition key IS the document ID) ───────

    [Fact]
    public async Task DeleteDocument_WhenPartitionKeyPathIsId_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        // Create database
        var dbId = NewId("db");
        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbRes = await fixture.Client.SendAsync(dbReq))
            dbRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create container with PK path = /id (same as document ID)
        var collId = NewId("coll");
        using (var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new { paths = new[] { "/id" }, kind = "Hash", version = 2 }
        }))
        using (var collRes = await fixture.Client.SendAsync(collReq))
            collRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create a document with a SHA-256-style hex ID
        var hexId = "fed886c04b0672b46b2c8a827023bffe81a8be89016fb09222fb216d156c1af0";
        using (var createReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            id = hexId,
            name = "vector-import"
        }))
        using (var createRes = await fixture.Client.SendAsync(createReq))
            createRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Query documents (cross-partition, same as Explorer)
        using var queryReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            query = "SELECT * FROM c",
            parameters = Array.Empty<object>()
        });
        queryReq.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        queryReq.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");

        using var queryRes = await fixture.Client.SendAsync(queryReq);
        queryRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var queryBody = await ReadBodyAsync(queryRes);
        var documents = queryBody["Documents"]!.AsArray();
        documents.Should().ContainSingle();

        // Extract PK from the document (same logic as Explorer's getPartitionKeyValue)
        var doc = documents[0]!.AsObject();
        var pkValue = doc["id"]!.GetValue<string>();
        pkValue.Should().Be(hexId);
        var pkHeader = $"[\"{pkValue}\"]";

        // DELETE using the extracted PK (same as Explorer)
        using var deleteReq = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}/docs/{hexId}");
        deleteReq.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, pkHeader);

        using var deleteRes = await fixture.Client.SendAsync(deleteReq);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteDocument_WhenPartitionKeyFieldValueEqualsId_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        // Create database
        var dbId = NewId("db");
        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbRes = await fixture.Client.SendAsync(dbReq))
            dbRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create container with PK path = /url_hash
        var collId = NewId("coll");
        using (var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new { paths = new[] { "/url_hash" }, kind = "Hash", version = 2 }
        }))
        using (var collRes = await fixture.Client.SendAsync(collReq))
            collRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create document where url_hash == id (like vector-store-imports scenario)
        var hexId = "fed886c04b0672b46b2c8a827023bffe81a8be89016fb09222fb216d156c1af0";
        using (var createReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            id = hexId,
            url_hash = hexId,
            name = "vector-import"
        }))
        using (var createRes = await fixture.Client.SendAsync(createReq))
            createRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Query documents (cross-partition, same as Explorer)
        using var queryReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            query = "SELECT * FROM c",
            parameters = Array.Empty<object>()
        });
        queryReq.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        queryReq.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");

        using var queryRes = await fixture.Client.SendAsync(queryReq);
        queryRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var queryBody = await ReadBodyAsync(queryRes);
        var documents = queryBody["Documents"]!.AsArray();
        documents.Should().ContainSingle();

        // Extract PK from the returned document (same as Explorer)
        var doc = documents[0]!.AsObject();
        var pkValue = doc["url_hash"]!.GetValue<string>();
        pkValue.Should().Be(hexId);
        var pkHeader = $"[\"{pkValue}\"]";

        // DELETE using the extracted PK value
        using var deleteReq = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}/docs/{hexId}");
        deleteReq.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, pkHeader);

        using var deleteRes = await fixture.Client.SendAsync(deleteReq);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─────── Exact user scenario: PK path /vector_store_id ───────

    [Fact]
    public async Task DeleteDocument_WithVectorStoreIdPartitionKey_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        // Create database
        var dbId = NewId("db");
        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbRes = await fixture.Client.SendAsync(dbReq))
            dbRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create container with PK path /vector_store_id (exact user scenario)
        var collId = "vector-store-imports";
        using (var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new { paths = new[] { "/vector_store_id" }, kind = "Hash", version = 2 }
        }))
        using (var collRes = await fixture.Client.SendAsync(collReq))
            collRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create document with SHA-256 hash ID and matching vector_store_id
        var hexId = "fed886c04b0672b46b2c8a827023bffe81a8be89016fb09222fb216d156c1af0";
        using (var createReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            id = hexId,
            vector_store_id = hexId,
            name = "test-import",
            status = "completed"
        }))
        using (var createRes = await fixture.Client.SendAsync(createReq))
            createRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Query documents via SELECT * FROM c (same as Explorer)
        using var queryReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            query = "SELECT * FROM c",
            parameters = Array.Empty<object>()
        });
        queryReq.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        queryReq.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");

        using var queryRes = await fixture.Client.SendAsync(queryReq);
        queryRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var queryBody = await ReadBodyAsync(queryRes);
        var documents = queryBody["Documents"]!.AsArray();
        documents.Should().ContainSingle();

        // Extract PK from the queried document (same as Explorer's getPartitionKeyValue)
        var doc = documents[0]!.AsObject();
        doc["vector_store_id"].Should().NotBeNull("queried document should have vector_store_id");
        var pkValue = doc["vector_store_id"]!.GetValue<string>();
        pkValue.Should().Be(hexId);

        // Build PK header exactly as the Explorer does: JSON.stringify([pkValue])
        var pkHeader = $"[\"{pkValue}\"]";

        // Delete the document (same as Explorer's cosmosClient.deleteDocument)
        using var deleteReq = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}/docs/{hexId}");
        deleteReq.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, pkHeader);

        using var deleteRes = await fixture.Client.SendAsync(deleteReq);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify the document is actually gone
        using var verifyReq = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}/colls/{collId}/docs/{hexId}");
        verifyReq.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, pkHeader);
        using var verifyRes = await fixture.Client.SendAsync(verifyReq);
        verifyRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteDocument_WithVectorStoreIdDifferentFromDocId_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        var dbId = NewId("db");
        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbRes = await fixture.Client.SendAsync(dbReq))
            dbRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var collId = "vector-store-imports";
        using (var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new { paths = new[] { "/vector_store_id" }, kind = "Hash", version = 2 }
        }))
        using (var collRes = await fixture.Client.SendAsync(collReq))
            collRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Document ID ≠ vector_store_id (different values)
        var docId = "abc123def456";
        var vsId = "vs_store_001";
        using (var createReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            id = docId,
            vector_store_id = vsId,
            name = "test-import"
        }))
        using (var createRes = await fixture.Client.SendAsync(createReq))
            createRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Query
        using var queryReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            query = "SELECT * FROM c",
            parameters = Array.Empty<object>()
        });
        queryReq.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        queryReq.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");
        using var queryRes = await fixture.Client.SendAsync(queryReq);
        queryRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var queryBody = await ReadBodyAsync(queryRes);
        var doc = queryBody["Documents"]!.AsArray()[0]!.AsObject();
        var pkValue = doc["vector_store_id"]!.GetValue<string>();
        pkValue.Should().Be(vsId);

        // Delete using the correct PK from query result
        using var deleteReq = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}/docs/{docId}");
        deleteReq.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, $"[\"{pkValue}\"]");

        using var deleteRes = await fixture.Client.SendAsync(deleteReq);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteDocument_WithMissingVectorStoreIdField_UsesNullPartitionKey()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        var dbId = NewId("db");
        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbRes = await fixture.Client.SendAsync(dbReq))
            dbRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var collId = "vector-store-imports";
        using (var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = collId,
            partitionKey = new { paths = new[] { "/vector_store_id" }, kind = "Hash", version = 2 }
        }))
        using (var collRes = await fixture.Client.SendAsync(collReq))
            collRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Document WITHOUT vector_store_id field → PK should be null
        var docId = "doc-without-pk";
        using (var createReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            id = docId,
            name = "no-pk-field"
        }))
        using (var createRes = await fixture.Client.SendAsync(createReq))
            createRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // Query — vector_store_id should be missing from the returned doc
        using var queryReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{collId}/docs", new
        {
            query = "SELECT * FROM c",
            parameters = Array.Empty<object>()
        });
        queryReq.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        queryReq.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");
        using var queryRes = await fixture.Client.SendAsync(queryReq);
        queryRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var queryBody = await ReadBodyAsync(queryRes);
        var doc = queryBody["Documents"]!.AsArray()[0]!.AsObject();
        // vector_store_id should NOT be in the document
        doc.ContainsKey("vector_store_id").Should().BeFalse("document was created without vector_store_id field");

        // Delete with null PK (matching what Explorer's getPartitionKeyValue would send)
        using var deleteReq = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}/colls/{collId}/docs/{docId}");
        deleteReq.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, "[null]");

        using var deleteRes = await fixture.Client.SendAsync(deleteReq);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─────── Special character document IDs ───────

    [Theory]
    [InlineData("doc.with.periods")]
    [InlineData("doc-with-hyphens-and-numbers-123")]
    [InlineData("fed886c04b0672b46b2c8a827023bffe81a8be89016fb09222fb216d156c1af0")]
    [InlineData("UPPER_and_lower_MiXeD")]
    [InlineData("a")]
    public async Task DeleteDocument_WithSpecialCharId_Succeeds(string specialId)
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Create document with special-char ID
        using (var createRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
        {
            id = specialId,
            tenantId = ids.PartitionKey,
            name = "special"
        }))
        {
            using var createResponse = await fixture.Client.SendAsync(createRequest);
            createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Delete the document using URL-encoded ID
        var encodedId = Uri.EscapeDataString(specialId);
        using var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{encodedId}");
        deleteRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey, PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());

        using var deleteResponse = await fixture.Client.SendAsync(deleteRequest);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
