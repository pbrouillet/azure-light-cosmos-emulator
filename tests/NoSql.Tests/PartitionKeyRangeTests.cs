using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class PartitionKeyRangeTests
{
    [Fact]
    public async Task ListPartitionKeyRanges_ReturnsSingleRangeWithETag()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        var collId = NewId("coll");
        await CreateDatabaseAsync(fixture, dbId);
        await CreateContainerAsync(fixture, dbId, collId);

        using var request = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}/colls/{collId}/pkranges");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();

        var body = await ReadBodyAsync(response);
        body["PartitionKeyRanges"]!.AsArray().Should().HaveCount(1);
        body["PartitionKeyRanges"]![0]!["id"]!.GetValue<string>().Should().Be("0");
    }

    [Fact]
    public async Task ListPartitionKeyRanges_WithMatchingIfNoneMatch_ReturnsNotModified()
    {
        // SDK clients (Python/.NET v3/Java/Go) read /pkranges as an incremental
        // change feed and only stop draining the routing map on HTTP 304. The
        // routing map is immutable in the emulator (no splits), so once the
        // client echoes the ETag in If-None-Match we must reply 304 to terminate
        // the drain — otherwise single-partition queries loop forever.
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        var collId = NewId("coll");
        await CreateDatabaseAsync(fixture, dbId);
        await CreateContainerAsync(fixture, dbId, collId);

        string etag;
        using (var first = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}/colls/{collId}/pkranges"))
        using (var firstResponse = await fixture.Client.SendAsync(first))
        {
            firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            firstResponse.Headers.ETag.Should().NotBeNull();
            etag = firstResponse.Headers.ETag!.ToString();
        }

        using var second = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}/colls/{collId}/pkranges");
        second.Headers.TryAddWithoutValidation(CosmosHeaders.IfNoneMatch, etag);
        using var secondResponse = await fixture.Client.SendAsync(second);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
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
