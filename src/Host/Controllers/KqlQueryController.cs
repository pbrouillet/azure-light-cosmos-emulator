using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Host.Kql;
using Azure.Cosmos.LightEmulator.Kql;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.Host.Controllers;

public sealed class KqlRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";
}

public sealed class KqlColumnResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public sealed class KqlResponse
{
    [JsonPropertyName("columns")]
    public IReadOnlyList<KqlColumnResponse> Columns { get; set; } = [];

    [JsonPropertyName("rows")]
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; set; } = [];
}

[ApiController]
[Route("api/emulator")]
public sealed class KqlQueryController(
    KqlQueryExecutor executor,
    IActivityStore activityStore,
    IQueryTelemetryStore telemetryStore,
    CosmosResponseHeaderService responseHeaders) : CosmosControllerBase(responseHeaders)
{
    [HttpPost("kql")]
    public async Task<IActionResult> ExecuteKql([FromBody] KqlRequest request, CancellationToken ct)
    {
        await SetCommonHeadersAsync(ct: ct);

        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { code = "BadRequest", message = "Query text is required." });

        try
        {
            var adapter = new MonitoringStorageAdapter(activityStore, telemetryStore);
            var result = await executor.ExecuteAsync(request.Query, adapter.ResolveTable);

            var columns = result.Schema.Columns
                .Select(c => new KqlColumnResponse { Name = c.Name, Type = c.KqlType })
                .ToList();

            var rows = result.Rows
                .Select(row => (IReadOnlyList<object?>)result.Schema.Columns
                    .Select(c => row.GetValueOrDefault(c.Name))
                    .ToList())
                .ToList();

            return Ok(new KqlResponse { Columns = columns, Rows = rows });
        }
        catch (KqlQueryException ex)
        {
            return BadRequest(new { code = "KqlParseError", message = ex.Message, diagnostics = ex.Diagnostics });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { code = "UnsupportedOperation", message = ex.Message });
        }
    }
}
