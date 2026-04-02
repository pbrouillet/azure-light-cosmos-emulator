using Azure.Cosmos.LightEmulator.Kql;
using FluentAssertions;

namespace Azure.Cosmos.LightEmulator.Kql.Tests;

public class KqlQueryExecutorTests
{
    private readonly KqlSchemaRegistry _registry;
    private readonly KqlQueryExecutor _executor;
    private readonly List<Dictionary<string, object?>> _sampleActivity;

    public KqlQueryExecutorTests()
    {
        _registry = new KqlSchemaRegistry();
        _registry.RegisterTable(new KqlTableSchema("activity",
        [
            new KqlColumnSchema("timestamp", "datetime"),
            new KqlColumnSchema("method", "string"),
            new KqlColumnSchema("path", "string"),
            new KqlColumnSchema("statusCode", "long"),
            new KqlColumnSchema("requestCharge", "real"),
            new KqlColumnSchema("latencyMs", "real"),
            new KqlColumnSchema("databaseId", "string"),
            new KqlColumnSchema("containerId", "string"),
        ]));
        _registry.RegisterTable(new KqlTableSchema("telemetry",
        [
            new KqlColumnSchema("timestamp", "datetime"),
            new KqlColumnSchema("databaseId", "string"),
            new KqlColumnSchema("containerId", "string"),
            new KqlColumnSchema("sqlText", "string"),
            new KqlColumnSchema("requestCharge", "real"),
            new KqlColumnSchema("latencyMs", "long"),
            new KqlColumnSchema("itemCount", "long"),
            new KqlColumnSchema("statusCode", "long"),
        ]));

        _executor = new KqlQueryExecutor(_registry);

        _sampleActivity = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-5),
                ["method"] = "GET",
                ["path"] = "/dbs",
                ["statusCode"] = 200L,
                ["requestCharge"] = 1.0,
                ["latencyMs"] = 5.2,
                ["databaseId"] = (string?)null,
                ["containerId"] = (string?)null,
            },
            new()
            {
                ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-4),
                ["method"] = "POST",
                ["path"] = "/dbs/testdb/colls/testcoll/docs",
                ["statusCode"] = 201L,
                ["requestCharge"] = 10.5,
                ["latencyMs"] = 12.3,
                ["databaseId"] = "testdb",
                ["containerId"] = "testcoll",
            },
            new()
            {
                ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-3),
                ["method"] = "GET",
                ["path"] = "/dbs/testdb/colls/testcoll/docs/doc1",
                ["statusCode"] = 404L,
                ["requestCharge"] = 1.0,
                ["latencyMs"] = 2.1,
                ["databaseId"] = "testdb",
                ["containerId"] = "testcoll",
            },
            new()
            {
                ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-2),
                ["method"] = "PUT",
                ["path"] = "/dbs/testdb/colls/testcoll/docs/doc2",
                ["statusCode"] = 200L,
                ["requestCharge"] = 5.0,
                ["latencyMs"] = 8.7,
                ["databaseId"] = "testdb",
                ["containerId"] = "testcoll",
            },
            new()
            {
                ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-1),
                ["method"] = "DELETE",
                ["path"] = "/dbs/testdb/colls/testcoll/docs/doc3",
                ["statusCode"] = 204L,
                ["requestCharge"] = 5.0,
                ["latencyMs"] = 3.5,
                ["databaseId"] = "testdb",
                ["containerId"] = "testcoll",
            },
        };
    }

    private IAsyncEnumerable<Dictionary<string, object?>> ResolveTable(string tableName)
    {
        if (tableName == "activity")
            return ToAsyncEnumerable(_sampleActivity);
        throw new InvalidOperationException($"Unknown table '{tableName}'");
    }

    private static async IAsyncEnumerable<Dictionary<string, object?>> ToAsyncEnumerable(
        IEnumerable<Dictionary<string, object?>> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SimpleTableScan_ReturnsAllRows()
    {
        var result = await _executor.ExecuteAsync("activity", ResolveTable);
        result.Rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Where_FiltersRows()
    {
        var result = await _executor.ExecuteAsync(
            "activity | where statusCode >= 400",
            ResolveTable);
        result.Rows.Should().HaveCount(1);
        result.Rows[0]["statusCode"].Should().Be(404L);
    }

    [Fact]
    public async Task Where_StringContains()
    {
        var result = await _executor.ExecuteAsync(
            "activity | where path contains \"docs\"",
            ResolveTable);
        result.Rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Project_SelectsColumns()
    {
        var result = await _executor.ExecuteAsync(
            "activity | project method, statusCode",
            ResolveTable);
        result.Rows.Should().HaveCount(5);
        result.Rows[0].Should().ContainKeys("method", "statusCode");
        result.Rows[0].Should().NotContainKey("path");
    }

    [Fact]
    public async Task Take_LimitsRows()
    {
        var result = await _executor.ExecuteAsync(
            "activity | take 2",
            ResolveTable);
        result.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Count_ReturnsCount()
    {
        var result = await _executor.ExecuteAsync(
            "activity | count",
            ResolveTable);
        result.Rows.Should().HaveCount(1);
        result.Rows[0]["Count"].Should().Be(5L);
    }

    [Fact]
    public async Task SortBy_OrdersDescending()
    {
        var result = await _executor.ExecuteAsync(
            "activity | sort by requestCharge desc",
            ResolveTable);
        result.Rows.Should().HaveCount(5);
        ((double)result.Rows[0]["requestCharge"]!).Should().Be(10.5);
    }

    [Fact]
    public async Task SortBy_OrdersAscending()
    {
        var result = await _executor.ExecuteAsync(
            "activity | sort by requestCharge asc",
            ResolveTable);
        result.Rows.Should().HaveCount(5);
        ((double)result.Rows[0]["requestCharge"]!).Should().BeLessThanOrEqualTo(1.0);
    }

    [Fact]
    public async Task Summarize_CountByMethod()
    {
        var result = await _executor.ExecuteAsync(
            "activity | summarize count() by method",
            ResolveTable);
        result.Rows.Should().HaveCountGreaterThanOrEqualTo(4); // GET, POST, PUT, DELETE
        var getRow = result.Rows.FirstOrDefault(r => (string?)r["method"] == "GET");
        getRow.Should().NotBeNull();
        getRow!["count_"].Should().Be(2L);
    }

    [Fact]
    public async Task Summarize_SumWithNoGroupBy()
    {
        var result = await _executor.ExecuteAsync(
            "activity | summarize totalRU = sum(requestCharge)",
            ResolveTable);
        result.Rows.Should().HaveCount(1);
        ((double)result.Rows[0]["totalRU"]!).Should().BeApproximately(22.5, 0.01);
    }

    [Fact]
    public async Task Summarize_AvgLatency()
    {
        var result = await _executor.ExecuteAsync(
            "activity | summarize avgLatency = avg(latencyMs)",
            ResolveTable);
        result.Rows.Should().HaveCount(1);
        var avg = (double)result.Rows[0]["avgLatency"]!;
        avg.Should().BeApproximately((5.2 + 12.3 + 2.1 + 8.7 + 3.5) / 5.0, 0.01);
    }

    [Fact]
    public async Task Extend_AddsColumn()
    {
        var result = await _executor.ExecuteAsync(
            "activity | extend isError = statusCode >= 400",
            ResolveTable);
        result.Rows.Should().HaveCount(5);
        result.Rows.Count(r => r["isError"] is true).Should().Be(1);
    }

    [Fact]
    public async Task Top_ReturnsTopN()
    {
        var result = await _executor.ExecuteAsync(
            "activity | top 2 by requestCharge desc",
            ResolveTable);
        result.Rows.Should().HaveCount(2);
        ((double)result.Rows[0]["requestCharge"]!).Should().Be(10.5);
    }

    [Fact]
    public async Task Distinct_ReturnsUniqueValues()
    {
        var result = await _executor.ExecuteAsync(
            "activity | distinct method",
            ResolveTable);
        result.Rows.Should().HaveCount(4); // GET, POST, PUT, DELETE
    }

    [Fact]
    public async Task PipelineComposition_WhereProjectSortTake()
    {
        var result = await _executor.ExecuteAsync(
            "activity | where statusCode == 200 | project method, requestCharge | sort by requestCharge desc | take 1",
            ResolveTable);
        result.Rows.Should().HaveCount(1);
        result.Rows[0]["method"].Should().Be("PUT");
        ((double)result.Rows[0]["requestCharge"]!).Should().Be(5.0);
    }

    [Fact]
    public async Task Summarize_Dcount()
    {
        var result = await _executor.ExecuteAsync(
            "activity | summarize methods = dcount(method)",
            ResolveTable);
        result.Rows.Should().HaveCount(1);
        result.Rows[0]["methods"].Should().Be(4L);
    }

    [Fact]
    public async Task InvalidQuery_ThrowsKqlQueryException()
    {
        var act = async () => await _executor.ExecuteAsync(
            "activity | where invalidColumn == 123",
            ResolveTable);
        await act.Should().ThrowAsync<KqlQueryException>();
    }

    [Fact]
    public async Task Schema_HasCorrectColumnInfo()
    {
        var result = await _executor.ExecuteAsync(
            "activity | project method, statusCode",
            ResolveTable);
        result.Schema.Columns.Should().HaveCount(2);
        result.Schema.Columns[0].Name.Should().Be("method");
        result.Schema.Columns[1].Name.Should().Be("statusCode");
    }
}
