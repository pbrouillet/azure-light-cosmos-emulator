using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class AccountMetadataTests
{
    [Fact]
    public async Task GetRoot_ReturnsAccountMetadata()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        body.Should().NotBeNull();

        body!["_self"].Should().NotBeNull();
        body["id"].Should().NotBeNull();
        body["_rid"].Should().NotBeNull();
        body["_dbs"]!.GetValue<string>().Should().Be("/dbs/");
        body["writableLocations"].Should().NotBeNull();
        body["writableLocations"]!.AsArray().Should().NotBeEmpty();
        body["readableLocations"].Should().NotBeNull();
        body["readableLocations"]!.AsArray().Should().NotBeEmpty();
        body["userConsistencyPolicy"].Should().NotBeNull();
        body["userConsistencyPolicy"]!["defaultConsistencyLevel"]!.GetValue<string>().Should().Be("Session");
    }

    [Fact]
    public async Task GetRoot_WritableLocations_ContainEndpoint()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        body.Should().NotBeNull();

        var location = body!["writableLocations"]!.AsArray()[0]!.AsObject();
        location["name"]!.GetValue<string>().Should().Be("Local");
        location["databaseAccountEndpoint"].Should().NotBeNull();
        location["databaseAccountEndpoint"]!.GetValue<string>().Should().EndWith("/");
    }

    [Fact]
    public async Task HeadRoot_ReturnsSuccessWithCosmosHeaders()
    {
        await using var fixture = await TestServerFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("x-ms-request-charge").Should().BeTrue();
        response.Headers.Contains("x-ms-activity-id").Should().BeTrue();
        response.Headers.Contains("x-ms-serviceversion").Should().BeTrue();
    }
}
