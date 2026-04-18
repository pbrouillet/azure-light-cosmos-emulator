using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class UniqueKeyDiagTests
{
    [Fact]
    public async Task ContainerWithUniqueKeyPolicy_HasPolicyInResponse()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = $"db-{Guid.NewGuid():N}";
        var containerId = $"coll-{Guid.NewGuid():N}";

        using (var dbReq = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId }))
        using (var dbResp = await fixture.Client.SendAsync(dbReq))
            dbResp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var collReq = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{dbId}/colls", new
        {
            id = containerId,
            partitionKey = new { paths = new[] { "/pk" }, kind = "Hash", version = 2 },
            uniqueKeyPolicy = new
            {
                uniqueKeys = new[]
                {
                    new { paths = new[] { "/email" } }
                }
            }
        });
        using var collResp = await fixture.Client.SendAsync(collReq);
        collResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await collResp.Content.ReadAsStringAsync();

        // Output full response for debugging
        content.Should().Contain("uniqueKeyPolicy", "Response should contain uniqueKeyPolicy");
    }
}
