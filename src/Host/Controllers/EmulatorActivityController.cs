using System.Globalization;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.Host.Controllers;

[ApiController]
[Route("api/emulator")]
public sealed class EmulatorActivityController(RuTracker ruTracker) : ControllerBase
{
    [HttpGet("activity")]
    public IActionResult GetActivity()
    {
        SetCommonHeaders();
        return Ok(ruTracker.GetRecentActivity());
    }

    private void SetCommonHeaders(double requestCharge = 1.0)
    {
        Response.Headers[CosmosHeaders.RequestCharge] = requestCharge.ToString("F2", CultureInfo.InvariantCulture);
        Response.Headers[CosmosHeaders.ActivityId] = Guid.NewGuid().ToString();
        Response.Headers[CosmosHeaders.ServiceVersion] = CosmosHeaders.CurrentServiceVersion;
        Response.Headers[CosmosHeaders.SchemaVersion] = CosmosHeaders.CurrentSchemaVersion;
    }
}
