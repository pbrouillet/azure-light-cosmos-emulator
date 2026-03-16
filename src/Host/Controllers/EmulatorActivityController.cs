using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.Host.Controllers;

[ApiController]
[Route("api/emulator")]
public sealed class EmulatorActivityController(
    RuTracker ruTracker,
    CosmosResponseHeaderService responseHeaders) : CosmosControllerBase(responseHeaders)
{
    [HttpGet("activity")]
    public async Task<IActionResult> GetActivity(CancellationToken ct)
    {
        await SetCommonHeadersAsync(ct: ct);
        return Ok(ruTracker.GetRecentActivity());
    }
}
