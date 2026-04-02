using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public class QueryTelemetryTests : IAsyncLifetime
{
    private TestServerFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = await TestServerFixture.CreateAsync();

        // Set up a database, container, and document for query tests
        var createDb = _fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = "telemetry-db" });
        await _fixture.Client.SendAsync(createDb);

        var createColl = _fixture.CreateRequest(HttpMethod.Post, "/dbs/telemetry-db/colls", new
        {
            id = "telemetry-coll",
            partitionKey = new { paths = new[] { "/pk" }, kind = "Hash" }
        });
        await _fixture.Client.SendAsync(createColl);

        var createDoc = _fixture.CreateRequest(HttpMethod.Post, "/dbs/telemetry-db/colls/telemetry-coll/docs", new
        {
            id = "doc1",
            pk = "a",
            name = "hello"
        });
        createDoc.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", "[\"a\"]");
        await _fixture.Client.SendAsync(createDoc);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Query_RecordsTelemetryEntry()
    {
        // Clear telemetry first
        var store = _fixture.GetService<IQueryTelemetryStore>();
        await store.ClearAsync();

        // Execute a query
        var queryRequest = _fixture.CreateRequest(HttpMethod.Post, "/dbs/telemetry-db/colls/telemetry-coll/docs", new
        {
            query = "SELECT * FROM c WHERE c.name = 'hello'"
        });
        queryRequest.Headers.TryAddWithoutValidation("x-ms-documentdb-isquery", "true");
        queryRequest.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", "[\"a\"]");
        queryRequest.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");
        var queryResponse = await _fixture.Client.SendAsync(queryRequest);
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Wait briefly for fire-and-forget telemetry to persist
        await Task.Delay(500);

        // Fetch telemetry from store
        var entries = await store.ListAsync();
        entries.Should().NotBeEmpty();

        var entry = entries[0];
        entry.DatabaseId.Should().Be("telemetry-db");
        entry.ContainerId.Should().Be("telemetry-coll");
        entry.SqlText.Should().Contain("SELECT * FROM c");
        entry.ConsistencyLevel.Should().Be("Session");
        entry.StatusCode.Should().Be(200);
        entry.RequestCharge.Should().BeGreaterThan(0);
        entry.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Telemetry_FilterByDatabase_ReturnsFiltered()
    {
        var store = _fixture.GetService<IQueryTelemetryStore>();
        await store.ClearAsync();

        // Record a telemetry entry directly to test store filtering
        await store.RecordAsync(new QueryTelemetryEntry
        {
            DatabaseId = "telemetry-db",
            ContainerId = "telemetry-coll",
            SqlText = "SELECT * FROM c",
            ConsistencyLevel = "Session",
            RequestCharge = 2.5,
            LatencyMs = 10,
            ItemCount = 1,
            StatusCode = 200,
            ActivityId = Guid.NewGuid().ToString()
        });

        await store.RecordAsync(new QueryTelemetryEntry
        {
            DatabaseId = "other-db",
            ContainerId = "other-coll",
            SqlText = "SELECT * FROM c",
            ConsistencyLevel = "Session",
            RequestCharge = 1.0,
            LatencyMs = 5,
            ItemCount = 0,
            StatusCode = 200,
            ActivityId = Guid.NewGuid().ToString()
        });

        // Filter by database
        var entries = await store.ListAsync(databaseId: "telemetry-db");
        entries.Should().NotBeEmpty("should find entries for telemetry-db");
        entries.Should().AllSatisfy(e => e.DatabaseId.Should().Be("telemetry-db"));
        entries.Should().AllSatisfy(e => e.DatabaseId.Should().Be("telemetry-db"));
    }

    [Fact]
    public async Task ClearTelemetry_RemovesAllEntries()
    {
        var store = _fixture.GetService<IQueryTelemetryStore>();

        // Execute a query first
        var queryRequest = _fixture.CreateRequest(HttpMethod.Post, "/dbs/telemetry-db/colls/telemetry-coll/docs", new
        {
            query = "SELECT * FROM c"
        });
        queryRequest.Headers.TryAddWithoutValidation("x-ms-documentdb-isquery", "true");
        queryRequest.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");
        await _fixture.Client.SendAsync(queryRequest);
        await Task.Delay(500);

        // Clear
        await store.ClearAsync();

        // Verify empty
        var entries = await store.ListAsync();
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_TelemetryEntry_ContainsConsistencyLevel()
    {
        var store = _fixture.GetService<IQueryTelemetryStore>();
        await store.ClearAsync();

        // Execute a query with Eventual consistency
        var queryRequest = _fixture.CreateRequest(HttpMethod.Post, "/dbs/telemetry-db/colls/telemetry-coll/docs", new
        {
            query = "SELECT * FROM c"
        });
        queryRequest.Headers.TryAddWithoutValidation("x-ms-documentdb-isquery", "true");
        queryRequest.Headers.TryAddWithoutValidation("x-ms-consistency-level", "Eventual");
        queryRequest.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");
        var response = await _fixture.Client.SendAsync(queryRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(500);

        var entries = await store.ListAsync();
        entries.Should().NotBeEmpty();
        entries[0].ConsistencyLevel.Should().Be("Eventual");
    }

    [Fact]
    public async Task Query_TelemetryEntry_ContainsQueryPlan()
    {
        var store = _fixture.GetService<IQueryTelemetryStore>();
        await store.ClearAsync();

        var queryRequest = _fixture.CreateRequest(HttpMethod.Post, "/dbs/telemetry-db/colls/telemetry-coll/docs", new
        {
            query = "SELECT * FROM c WHERE c.name = 'hello'"
        });
        queryRequest.Headers.TryAddWithoutValidation("x-ms-documentdb-isquery", "true");
        queryRequest.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");
        var response = await _fixture.Client.SendAsync(queryRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(1500);

        var entries = await store.ListAsync();
        entries.Should().NotBeEmpty();

        var entry = entries[0];
        entry.QueryPlan.Should().NotBeNullOrEmpty("query plan should be generated and stored");
        entry.QueryPlan.Should().Contain("estimatedRuCharge", "plan should contain RU estimate");
        entry.QueryPlan.Should().Contain("indexAnalysis", "plan should contain index analysis");
    }
}
