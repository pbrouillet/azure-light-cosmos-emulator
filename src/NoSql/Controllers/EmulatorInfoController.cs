using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("api/emulator")]
public sealed class EmulatorInfoController(
    IEmulatorInfoService emulatorInfoService,
    IDocumentStore documentStore,
    QueryExplainService queryExplainService,
    CosmosResponseHeaderService responseHeaders) : CosmosControllerBase(responseHeaders)
{
    [HttpGet("info")]
    public async Task<IActionResult> GetInfo(CancellationToken ct)
    {
        await SetCommonHeadersAsync(ct: ct);
        return Ok(await emulatorInfoService.GetInfoAsync(ct));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        await SetCommonHeadersAsync(ct: ct);
        return Ok(await emulatorInfoService.GetStatsAsync(ct));
    }

    [HttpPost("explain")]
    public async Task<IActionResult> ExplainQuery([FromBody] QueryExplainRequest? request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.DatabaseId))
        {
            ModelState.AddModelError(nameof(QueryExplainRequest.DatabaseId), "The databaseId field is required.");
        }

        if (string.IsNullOrWhiteSpace(request?.ContainerId))
        {
            ModelState.AddModelError(nameof(QueryExplainRequest.ContainerId), "The containerId field is required.");
        }

        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            ModelState.AddModelError(nameof(QueryExplainRequest.Query), "The query field is required.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var result = await queryExplainService.ExplainAsync(request!.DatabaseId!, request.ContainerId!, request.Query!, ct);
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
        {
            RequestCharge = result.EstimatedRuCharge.Total,
            DatabaseId = request.DatabaseId,
            ContainerId = request.ContainerId
        }, ct);

        return Ok(result);
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateEmulatorSettingsRequest? request, CancellationToken ct)
    {
        if (request?.EnableEntraId is null)
        {
            ModelState.AddModelError(nameof(UpdateEmulatorSettingsRequest.EnableEntraId), "The enableEntraId field is required.");
            return ValidationProblem(ModelState);
        }

        await SetCommonHeadersAsync(ct: ct);
        return Ok(await emulatorInfoService.UpdateSettingsAsync(
            request.EnableEntraId.Value,
            request.TenantId,
            request.ClientId,
            ct));
    }

    [HttpPut("throughput/database/{dbId}")]
    public async Task<IActionResult> UpdateDatabaseThroughput(string dbId, [FromBody] UpdateThroughputRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { code = "BadRequest", message = "Request body is required." });

        var database = await documentStore.GetDatabaseAsync(dbId, ct);
        database.MaxThroughput = request.MaxThroughput;
        var updated = await documentStore.ReplaceDatabaseAsync(database, ct);

        await SetCommonHeadersAsync(ct: ct);
        return Ok(new { id = updated.Id, maxThroughput = updated.MaxThroughput });
    }

    [HttpPut("throughput/container/{dbId}/{collId}")]
    public async Task<IActionResult> UpdateContainerThroughput(string dbId, string collId, [FromBody] UpdateThroughputRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { code = "BadRequest", message = "Request body is required." });

        var container = await documentStore.GetContainerAsync(dbId, collId, ct);
        container.MaxThroughput = request.MaxThroughput ?? 400;
        var updated = await documentStore.ReplaceContainerAsync(dbId, container, ct);

        await SetCommonHeadersAsync(ct: ct);
        return Ok(new { id = updated.Id, databaseId = dbId, maxThroughput = updated.MaxThroughput });
    }

    [HttpGet("throughput/database/{dbId}")]
    public async Task<IActionResult> GetDatabaseThroughput(string dbId, CancellationToken ct)
    {
        var database = await documentStore.GetDatabaseAsync(dbId, ct);
        await SetCommonHeadersAsync(ct: ct);
        return Ok(new { id = database.Id, maxThroughput = database.MaxThroughput });
    }

    [HttpGet("throughput/container/{dbId}/{collId}")]
    public async Task<IActionResult> GetContainerThroughput(string dbId, string collId, CancellationToken ct)
    {
        var container = await documentStore.GetContainerAsync(dbId, collId, ct);
        await SetCommonHeadersAsync(ct: ct);
        return Ok(new { id = container.Id, databaseId = dbId, maxThroughput = container.MaxThroughput });
    }
}

public sealed class QueryExplainRequest
{
    [JsonPropertyName("databaseId")]
    public string? DatabaseId { get; set; }

    [JsonPropertyName("containerId")]
    public string? ContainerId { get; set; }

    [JsonPropertyName("query")]
    public string? Query { get; set; }
}

public sealed class UpdateEmulatorSettingsRequest
{
    [JsonPropertyName("enableEntraId")]
    public bool? EnableEntraId { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }
}

public sealed class UpdateThroughputRequest
{
    [JsonPropertyName("maxThroughput")]
    public int? MaxThroughput { get; set; }
}
