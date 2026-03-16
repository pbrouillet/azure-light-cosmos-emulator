using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class QueryExplainEndpointTests
{
    [Fact]
    public async Task ExplainQuery_ReturnsEducationalPlanAndRecommendations()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        const string query = "SELECT c.name, COUNT(1) FROM c JOIN t IN c.tags WHERE c.age > 25 OR STARTSWITH(c.name, 'A') GROUP BY c.name ORDER BY c.name";

        using var request = fixture.CreateRequest(HttpMethod.Post, "/api/emulator/explain", new
        {
            databaseId = ids.DatabaseId,
            containerId = ids.ContainerId,
            query
        });

        var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("x-ms-request-charge", out var requestCharges).Should().BeTrue();
        requestCharges.Should().ContainSingle();

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        body.Should().NotBeNull();
        body!["query"]!.GetValue<string>().Should().Be(query);

        var queryPlan = body["queryPlan"]!.AsObject();
        queryPlan["type"]!.GetValue<string>().Should().Be("select");
        queryPlan["source"]!.GetValue<string>().Should().Be("c");
        queryPlan["join"]!.AsObject()["alias"]!.GetValue<string>().Should().Be("t");
        queryPlan["groupBy"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Contain("c.name");
        queryPlan["aggregates"]!.AsArray().Select(node => node!.GetValue<string>()).Should().Contain("COUNT(1)");

        var estimatedRuCharge = body["estimatedRuCharge"]!.AsObject();
        estimatedRuCharge["total"]!.GetValue<double>().Should().BeGreaterThan(0);
        estimatedRuCharge["joinCost"]!.GetValue<double>().Should().BeGreaterThan(0);

        var recommendations = body["indexAnalysis"]!["recommendations"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        recommendations.Should().Contain(item => item.Contains("range index on /age", StringComparison.Ordinal));
        recommendations.Should().Contain(item => item.Contains("STARTSWITH", StringComparison.Ordinal));
        recommendations.Should().Contain(item => item.Contains("JOIN operations", StringComparison.Ordinal));

        var warnings = body["warnings"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        warnings.Should().Contain(item => item.Contains("Cross-partition query detected", StringComparison.Ordinal));
        warnings.Should().Contain(item => item.Contains("GROUP BY", StringComparison.Ordinal));

        var notes = body["educationalNotes"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        notes.Should().Contain(item => item.Contains("JOIN", StringComparison.Ordinal));
        notes.Should().Contain(item => item.Contains("OR operator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplainQuery_BypassesAuthentication()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var ids = await CreateContainerHierarchyAsync(fixture);
        using var unauthenticatedClient = fixture.CreateUnauthenticatedClient();

        var response = await unauthenticatedClient.PostAsJsonAsync("/api/emulator/explain", new
        {
            databaseId = ids.DatabaseId,
            containerId = ids.ContainerId,
            query = "SELECT * FROM c"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        body!["queryPlan"]!["type"]!.GetValue<string>().Should().Be("select");
    }

    private static async Task<(string DatabaseId, string ContainerId)> CreateContainerHierarchyAsync(TestServerFixture fixture)
    {
        var databaseId = $"db-{Guid.NewGuid():N}";
        var containerId = $"coll-{Guid.NewGuid():N}";

        using (var dbRequest = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = databaseId }))
        {
            var dbResponse = await fixture.Client.SendAsync(dbRequest);
            dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using (var containerRequest = fixture.CreateRequest(HttpMethod.Post, $"/dbs/{databaseId}/colls", new
        {
            id = containerId,
            partitionKey = new
            {
                paths = new[] { "/tenantId" },
                kind = "Hash",
                version = 2
            }
        }))
        {
            var containerResponse = await fixture.Client.SendAsync(containerRequest);
            containerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return (databaseId, containerId);
    }
}
