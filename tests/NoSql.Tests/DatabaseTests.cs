using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class DatabaseTests
{
    [Fact]
    public async Task CreateDatabase_ReturnsCreatedDatabase()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");

        using var request = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadBodyAsync(response);
        body["id"]!.GetValue<string>().Should().Be(dbId);
        body["_self"]!.GetValue<string>().Should().StartWith("dbs/");
    }

    [Fact]
    public async Task ListDatabases_ReturnsCreatedDatabases()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbA = NewId("db");
        var dbB = NewId("db");

        await CreateDatabaseAsync(fixture, dbA);
        await CreateDatabaseAsync(fixture, dbB);

        using var request = fixture.CreateRequest(HttpMethod.Get, "/dbs");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        var ids = body["Databases"]!
            .AsArray()
            .Select(database => database!["id"]!.GetValue<string>())
            .ToArray();

        ids.Should().BeEquivalentTo(new[] { dbA, dbB });
        body["_count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task GetDatabase_ReturnsExistingDatabase()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        await CreateDatabaseAsync(fixture, dbId);

        using var request = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadBodyAsync(response);
        body["id"]!.GetValue<string>().Should().Be(dbId);
    }

    [Fact]
    public async Task DeleteDatabase_RemovesDatabase()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        await CreateDatabaseAsync(fixture, dbId);

        using (var deleteRequest = fixture.CreateRequest(HttpMethod.Delete, $"/dbs/{dbId}"))
        using (var deleteResponse = await fixture.Client.SendAsync(deleteRequest))
        {
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var getRequest = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}");
        using var getResponse = await fixture.Client.SendAsync(getRequest);

        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateDuplicateDatabase_ReturnsConflict()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");
        await CreateDatabaseAsync(fixture, dbId);

        using var request = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId });
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await ReadBodyAsync(response);
        body["code"]!.GetValue<string>().Should().Be("Conflict");
    }

    [Fact]
    public async Task GetMissingDatabase_ReturnsNotFound()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = NewId("db");

        using var request = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}");
        using var response = await fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ReadBodyAsync(response);
        body["code"]!.GetValue<string>().Should().Be("NotFound");
    }

    [Fact]
    public async Task CreateAndGetDatabase_WithMixedCaseName_AuthSucceeds()
    {
        await using var fixture = await TestServerFixture.CreateAsync();
        var dbId = "MyDatabase-" + Guid.NewGuid().ToString("N")[..8];

        using var createRequest = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId });
        using var createResponse = await fixture.Client.SendAsync(createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var getRequest = fixture.CreateRequest(HttpMethod.Get, $"/dbs/{dbId}");
        using var getResponse = await fixture.Client.SendAsync(getRequest);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadBodyAsync(getResponse);
        body["id"]!.GetValue<string>().Should().Be(dbId);
    }

    private static async Task CreateDatabaseAsync(TestServerFixture fixture, string dbId)
    {
        using var request = fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = dbId });
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
