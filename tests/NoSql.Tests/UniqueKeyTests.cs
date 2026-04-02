using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class UniqueKeyTests
{
    [Fact]
    public async Task CreateDocument_ViolatesUniqueKey_ReturnsConflict()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerWithUniqueKeyAsync(fixture, ["/email"]);

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), pk = ids.PartitionKey, email = "a@b.com" });

        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = NewId("doc"), pk = ids.PartitionKey, email = "a@b.com" });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateDocument_SameUniqueKeyDifferentPartition_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerWithUniqueKeyAsync(fixture, ["/email"]);

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), pk = "partition-a", email = "a@b.com" });

        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = NewId("doc"), pk = "partition-b", email = "a@b.com" });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateDocument_DifferentUniqueKeyValues_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerWithUniqueKeyAsync(fixture, ["/email"]);

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), pk = ids.PartitionKey, email = "a@b.com" });

        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = NewId("doc"), pk = ids.PartitionKey, email = "c@d.com" });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateDocument_CompoundUniqueKeyConflict_ReturnsConflict()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerWithUniqueKeyAsync(fixture, ["/lastName", "/firstName"]);

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), pk = ids.PartitionKey, lastName = "Smith", firstName = "John" });

        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = NewId("doc"), pk = ids.PartitionKey, lastName = "Smith", firstName = "John" });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateDocument_CompoundUniqueKeyPartialMatch_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerWithUniqueKeyAsync(fixture, ["/lastName", "/firstName"]);

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), pk = ids.PartitionKey, lastName = "Smith", firstName = "John" });

        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = NewId("doc"), pk = ids.PartitionKey, lastName = "Smith", firstName = "Jane" });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ReplaceDocument_SameUniqueKeyValues_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerWithUniqueKeyAsync(fixture, ["/email"]);
        var docId = NewId("doc");

        await CreateDocAsync(fixture, ids, new { id = docId, pk = ids.PartitionKey, email = "a@b.com", name = "original" });

        using var request = fixture.CreateRequest(HttpMethod.Put,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs/{docId}",
            new { id = docId, pk = ids.PartitionKey, email = "a@b.com", name = "updated" });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateDocument_NullUniqueKeyConflict_ReturnsConflict()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerWithUniqueKeyAsync(fixture, ["/email"]);

        // Both documents lack the 'email' field, so both have null → conflict
        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), pk = ids.PartitionKey, name = "first" });

        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs",
            new { id = NewId("doc"), pk = ids.PartitionKey, name = "second" });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateDocument_NoUniqueKeyPolicy_Succeeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        var containerId = NewId("coll");

        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbResp = await fixture.Client.SendAsync(dbReq))
            dbResp.StatusCode.Should().Be(HttpStatusCode.Created);

        using (var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = containerId,
            partitionKey = new { paths = new[] { "/pk" }, kind = "Hash", version = 2 }
        }))
        using (var collResp = await fixture.Client.SendAsync(collReq))
            collResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Create two documents with same "email" — should succeed since no unique key policy
        using (var req1 = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{containerId}/docs",
            new { id = NewId("doc"), pk = "p1", email = "a@b.com" }))
        using (var resp1 = await fixture.Client.SendAsync(req1))
            resp1.StatusCode.Should().Be(HttpStatusCode.Created);

        using var req2 = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls/{containerId}/docs",
            new { id = NewId("doc"), pk = "p1", email = "a@b.com" });
        using var resp2 = await fixture.Client.SendAsync(req2);

        resp2.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static async Task<(string DatabaseId, string ContainerId, string PartitionKey)> CreateContainerWithUniqueKeyAsync(
        TestServerFixture fixture, string[] uniqueKeyPaths)
    {
        var dbId = NewId("db");
        var containerId = NewId("coll");
        const string partitionKey = "pk-value";

        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbResp = await fixture.Client.SendAsync(dbReq))
            dbResp.StatusCode.Should().Be(HttpStatusCode.Created);

        using (var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = containerId,
            partitionKey = new { paths = new[] { "/pk" }, kind = "Hash", version = 2 },
            uniqueKeyPolicy = new
            {
                uniqueKeys = new[]
                {
                    new { paths = uniqueKeyPaths }
                }
            }
        }))
        using (var collResp = await fixture.Client.SendAsync(collReq))
            collResp.StatusCode.Should().Be(HttpStatusCode.Created);

        return (dbId, containerId, partitionKey);
    }

    private static async Task CreateDocAsync(TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids, object body)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", body);
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
