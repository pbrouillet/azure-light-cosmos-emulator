using System.Net;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class UdfQueryTests
{
    [Fact]
    public async Task SelectWithUdf_ReturnsComputedValue()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Create a UDF that doubles a number
        using (var udfRequest = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/udfs", new
            {
                id = "doubleIt",
                body = "function doubleIt(x) { return x * 2; }"
            }))
        {
            using var udfResponse = await fixture.Client.SendAsync(udfRequest);
            udfResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Create a document
        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), tenantId = ids.PartitionKey, amount = 50 });

        // Query using the UDF with SELECT VALUE to get direct result
        var result = await QueryAsync(fixture, ids, "SELECT VALUE udf.doubleIt(c.amount) FROM c");
        result["_count"]!.GetValue<int>().Should().Be(1);

        var documents = (result["Documents"] ?? result["documents"])!.AsArray();
        documents[0]!.GetValue<double>().Should().Be(100);
    }

    [Fact]
    public async Task SelectWithUdf_MultipleArguments_Works()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Create a UDF that adds two values
        using (var udfRequest = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/udfs", new
            {
                id = "addValues",
                body = "function addValues(a, b) { return a + b; }"
            }))
        {
            using var udfResponse = await fixture.Client.SendAsync(udfRequest);
            udfResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), tenantId = ids.PartitionKey, price = 100, tax = 15 });

        var result = await QueryAsync(fixture, ids, "SELECT VALUE udf.addValues(c.price, c.tax) FROM c");
        result["_count"]!.GetValue<int>().Should().Be(1);

        var documents = (result["Documents"] ?? result["documents"])!.AsArray();
        documents[0]!.GetValue<double>().Should().Be(115);
    }

    [Fact]
    public async Task SelectWithNonExistentUdf_ReturnsError()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), tenantId = ids.PartitionKey, value = 1 });

        // Query using a non-existent UDF should fail
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", new
            {
                query = "SELECT VALUE udf.nonExistent(c.value) FROM c"
            });
        request.Headers.TryAddWithoutValidation(CosmosHeaders.IsQuery, "true");
        request.Headers.TryAddWithoutValidation(CosmosHeaders.EnableCrossPartition, "true");
        request.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CosmosHeaders.QueryJsonContentType);

        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WhereWithUdf_FiltersDocuments()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);

        // Create a UDF that checks if a value is positive
        using (var udfRequest = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/udfs", new
            {
                id = "isPositive",
                body = "function isPositive(x) { return x > 0; }"
            }))
        {
            using var udfResponse = await fixture.Client.SendAsync(udfRequest);
            udfResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), tenantId = ids.PartitionKey, amount = 10 });
        await CreateDocAsync(fixture, ids, new { id = NewId("doc"), tenantId = ids.PartitionKey, amount = -5 });

        var result = await QueryAsync(fixture, ids, "SELECT * FROM c WHERE udf.isPositive(c.amount)");
        result["_count"]!.GetValue<int>().Should().Be(1);
    }

    // ─── Helpers ────────────────────────────────────────────────────

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

    private static async Task CreateDocAsync(TestServerFixture fixture,
        (string DatabaseId, string ContainerId, string PartitionKey) ids, object body)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post,
            $"/dbs/{ids.DatabaseId}/colls/{ids.ContainerId}/docs", body);
        using var response = await fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
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
