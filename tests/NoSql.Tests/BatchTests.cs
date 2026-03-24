using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class BatchTests
{
    [Fact]
    public async Task Batch_CreateMultipleDocuments_ReturnsAllCreated()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var doc1Id = NewId("doc");
        var doc2Id = NewId("doc");

        using var request = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = doc1Id,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Alice"
                }
            },
            new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = doc2Id,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Bob"
                }
            }
        ]);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await ReadArrayAsync(response);
        results.Count.Should().Be(2);
        results[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(201);
        results[1]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(201);
        results[0]!.AsObject()["resourceBody"]!.AsObject()["id"]!.GetValue<string>().Should().Be(doc1Id);
        results[1]!.AsObject()["resourceBody"]!.AsObject()["id"]!.GetValue<string>().Should().Be(doc2Id);
    }

    [Fact]
    public async Task Batch_CreateAndRead_ReturnsSuccess()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var docId = NewId("doc");

        using var request = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = docId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Alice"
                }
            },
            new JsonObject
            {
                ["operationType"] = "Read",
                ["id"] = docId
            }
        ]);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await ReadArrayAsync(response);
        results.Count.Should().Be(2);
        results[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(201);
        results[1]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(200);
        results[1]!.AsObject()["resourceBody"]!.AsObject()["name"]!.GetValue<string>().Should().Be("Alice");
    }

    [Fact]
    public async Task Batch_CreateAndReplace_ReturnsSuccess()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var docId = NewId("doc");

        using var request = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = docId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Alice"
                }
            },
            new JsonObject
            {
                ["operationType"] = "Replace",
                ["id"] = docId,
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = docId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Updated"
                }
            }
        ]);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await ReadArrayAsync(response);
        results.Count.Should().Be(2);
        results[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(201);
        results[1]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(200);
        results[1]!.AsObject()["resourceBody"]!.AsObject()["name"]!.GetValue<string>().Should().Be("Updated");
    }

    [Fact]
    public async Task Batch_WithConflict_RollsBackAllOperations()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Pre-create a document that will cause a conflict
        var existingDocId = await CreateDocumentAsync(fixture, ids, "existing");

        var newDocId = NewId("doc");

        using var request = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = newDocId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "New"
                }
            },
            new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = existingDocId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Duplicate"
                }
            }
        ]);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await ReadArrayAsync(response);
        results.Count.Should().Be(2);
        // First op rolled back → 424
        results[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(424);
        // Second op failed → 409 Conflict
        results[1]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(409);

        // Verify the first document was rolled back (should not exist)
        using var readRequest = fixture.CreateRequest(HttpMethod.Get,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{newDocId}");
        readRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        using var readResponse = await fixture.Client.SendAsync(readRequest);
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Batch_ExceedingMaxOperations_ReturnsBadRequest()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var operations = new JsonArray();
        for (var i = 0; i < 101; i++)
        {
            operations.Add(new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = NewId("doc"),
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = $"doc-{i}"
                }
            });
        }

        using var request = CreateBatchRequest(fixture, ids, operations);
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Batch_WithUpsert_ReturnsSuccess()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var docId = NewId("doc");

        // First upsert (create)
        using var request1 = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Upsert",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = docId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Created"
                }
            }
        ]);

        using var response1 = await fixture.Client.SendAsync(request1);
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        var results1 = await ReadArrayAsync(response1);
        results1[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(201);

        // Second upsert (replace)
        using var request2 = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Upsert",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = docId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Replaced"
                }
            }
        ]);

        using var response2 = await fixture.Client.SendAsync(request2);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);
        var results2 = await ReadArrayAsync(response2);
        results2[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(200);
        results2[0]!.AsObject()["resourceBody"]!.AsObject()["name"]!.GetValue<string>().Should().Be("Replaced");
    }

    [Fact]
    public async Task Batch_WithDelete_ReturnsSuccess()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var docId = await CreateDocumentAsync(fixture, ids, "to-delete");

        using var request = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Delete",
                ["id"] = docId
            }
        ]);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await ReadArrayAsync(response);
        results.Count.Should().Be(1);
        results[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(204);

        // Verify document is gone
        using var readRequest = fixture.CreateRequest(HttpMethod.Get,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}");
        readRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        using var readResponse = await fixture.Client.SendAsync(readRequest);
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Batch_WithPatch_ReturnsSuccess()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var docId = await CreateDocumentAsync(fixture, ids, "patchable");

        using var request = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Patch",
                ["id"] = docId,
                ["resourceBody"] = new JsonObject
                {
                    ["operations"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["op"] = "set",
                            ["path"] = "/name",
                            ["value"] = "Patched"
                        }
                    }
                }
            }
        ]);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await ReadArrayAsync(response);
        results.Count.Should().Be(1);
        results[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(200);
        results[0]!.AsObject()["resourceBody"]!.AsObject()["name"]!.GetValue<string>().Should().Be("Patched");
    }

    [Fact]
    public async Task Batch_RollbackVerification_CreatedDocDoesNotExist()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        var doc1Id = NewId("doc");
        var doc2Id = NewId("doc");
        var nonExistentId = NewId("nonexistent");

        // doc1 create succeeds, then replace of non-existent doc fails → doc1 rolled back
        using var request = CreateBatchRequest(fixture, ids, [
            new JsonObject
            {
                ["operationType"] = "Create",
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = doc1Id,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "Doc1"
                }
            },
            new JsonObject
            {
                ["operationType"] = "Replace",
                ["id"] = nonExistentId,
                ["resourceBody"] = new JsonObject
                {
                    ["id"] = nonExistentId,
                    ["tenantId"] = ids.PartitionKey,
                    ["name"] = "NoExist"
                }
            }
        ]);

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await ReadArrayAsync(response);
        results[0]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(424);
        results[1]!.AsObject()["statusCode"]!.GetValue<int>().Should().Be(404);

        // Verify doc1 was rolled back
        using var readRequest = fixture.CreateRequest(HttpMethod.Get,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{doc1Id}");
        readRequest.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        using var readResponse = await fixture.Client.SendAsync(readRequest);
        readResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Batch_WithoutBatchHeader_ReturnsNotFound()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // POST to collection endpoint without batch header
        var request = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}",
            new[] { new { operationType = "Create", resourceBody = new { id = "test", tenantId = ids.PartitionKey } } });

        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static HttpRequestMessage CreateBatchRequest(
        TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids,
        JsonArray operations)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}");
        request.Content = new StringContent(operations.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsBatchRequest, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.PartitionKey,
            PartitionKeyValue.Create(ids.PartitionKey).ToHeaderString());
        return request;
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

    private static async Task<string> CreateDocumentAsync(
        TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids,
        string name)
    {
        var docId = NewId("doc");
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = docId, tenantId = ids.PartitionKey, name });
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return docId;
    }

    private static async Task<JsonArray> ReadArrayAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(content);
        node.Should().NotBeNull();
        return node!.AsArray();
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
