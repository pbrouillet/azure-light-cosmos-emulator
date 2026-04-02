using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.Host.Controllers;

[ApiController]
[Route("api/emulator")]
public sealed class QueryTelemetryController(
    IQueryTelemetryStore telemetryStore,
    CosmosResponseHeaderService responseHeaders) : CosmosControllerBase(responseHeaders)
{
    [HttpGet("telemetry")]
    public async Task<IActionResult> GetTelemetry(
        [FromQuery] string? db = null,
        [FromQuery] string? container = null,
        [FromQuery] int max = 100,
        CancellationToken ct = default)
    {
        await SetCommonHeadersAsync(ct: ct);
        var entries = await telemetryStore.ListAsync(db, container, max, ct);
        return Ok(entries);
    }

    [HttpDelete("telemetry")]
    public async Task<IActionResult> ClearTelemetry(CancellationToken ct)
    {
        await telemetryStore.ClearAsync(ct);
        await SetCommonHeadersAsync(ct: ct);
        return NoContent();
    }
}
