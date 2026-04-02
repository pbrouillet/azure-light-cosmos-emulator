using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class ConsistencyMiddlewareTests : IAsyncLifetime
{
    private TestServerFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await TestServerFixture.CreateAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Request_WithNoConsistencyHeader_UsesDefault()
    {
        // Arrange — create a database to test against
        var createDb = _fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = "consistency-test-db" });
        var dbResponse = await _fixture.Client.SendAsync(createDb);
        dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act — read with no consistency header
        var getDb = _fixture.CreateRequest(HttpMethod.Get, "/dbs/consistency-test-db");
        var response = await _fixture.Client.SendAsync(getDb);

        // Assert — should succeed (default Session consistency)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Request_WithWeakerConsistency_Succeeds()
    {
        // Arrange
        var createDb = _fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = "weaker-consistency-db" });
        var dbResponse = await _fixture.Client.SendAsync(createDb);
        dbResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act — request Eventual consistency (weaker than default Session)
        var getDb = _fixture.CreateRequest(HttpMethod.Get, "/dbs/weaker-consistency-db");
        getDb.Headers.TryAddWithoutValidation("x-ms-consistency-level", "Eventual");
        var response = await _fixture.Client.SendAsync(getDb);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Request_WithStrongerConsistency_Returns400()
    {
        // Act — request Strong consistency (stronger than default Session)
        var getDb = _fixture.CreateRequest(HttpMethod.Get, "/dbs");
        getDb.Headers.TryAddWithoutValidation("x-ms-consistency-level", "Strong");
        var response = await _fixture.Client.SendAsync(getDb);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        body!["code"]!.GetValue<string>().Should().Be("BadRequest");
        body["message"]!.GetValue<string>().Should().Contain("stronger than the account default");
    }

    [Fact]
    public async Task Request_WithBoundedStaleness_Returns400_WhenDefaultIsSession()
    {
        // BoundedStaleness is stronger than Session
        var getDb = _fixture.CreateRequest(HttpMethod.Get, "/dbs");
        getDb.Headers.TryAddWithoutValidation("x-ms-consistency-level", "BoundedStaleness");
        var response = await _fixture.Client.SendAsync(getDb);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Request_WithConsistentPrefix_Succeeds()
    {
        // ConsistentPrefix is weaker than Session
        var createDb = _fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = "prefix-consistency-db" });
        await _fixture.Client.SendAsync(createDb);

        var getDb = _fixture.CreateRequest(HttpMethod.Get, "/dbs/prefix-consistency-db");
        getDb.Headers.TryAddWithoutValidation("x-ms-consistency-level", "ConsistentPrefix");
        var response = await _fixture.Client.SendAsync(getDb);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Query_EmitsSessionToken()
    {
        // Arrange — create database, container, document
        var createDb = _fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = "session-token-db" });
        await _fixture.Client.SendAsync(createDb);

        var createColl = _fixture.CreateRequest(HttpMethod.Post, "/dbs/session-token-db/colls", new
        {
            id = "session-token-coll",
            partitionKey = new { paths = new[] { "/pk" }, kind = "Hash" }
        });
        await _fixture.Client.SendAsync(createColl);

        var createDoc = _fixture.CreateRequest(HttpMethod.Post, "/dbs/session-token-db/colls/session-token-coll/docs", new
        {
            id = "doc1",
            pk = "a",
            name = "test"
        });
        createDoc.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", "[\"a\"]");
        await _fixture.Client.SendAsync(createDoc);

        // Act — query
        var queryRequest = _fixture.CreateRequest(HttpMethod.Post, "/dbs/session-token-db/colls/session-token-coll/docs", new
        {
            query = "SELECT * FROM c"
        });
        queryRequest.Headers.TryAddWithoutValidation("x-ms-documentdb-isquery", "true");
        queryRequest.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");
        var response = await _fixture.Client.SendAsync(queryRequest);

        // Assert — session token should be present
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("x-ms-session-token").Should().BeTrue("queries should now emit session tokens");
    }
}
