using System.Globalization;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("api/emulator")]
public sealed class EmulatorInfoController(IEmulatorInfoService emulatorInfoService) : ControllerBase
{
    [HttpGet("info")]
    public async Task<IActionResult> GetInfo(CancellationToken ct)
    {
        SetCommonHeaders();
        return Ok(await emulatorInfoService.GetInfoAsync(ct));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        SetCommonHeaders();
        return Ok(await emulatorInfoService.GetStatsAsync(ct));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateEmulatorSettingsRequest? request, CancellationToken ct)
    {
        if (request?.EnableEntraId is null)
        {
            ModelState.AddModelError(nameof(UpdateEmulatorSettingsRequest.EnableEntraId), "The enableEntraId field is required.");
            return ValidationProblem(ModelState);
        }

        SetCommonHeaders();
        return Ok(await emulatorInfoService.UpdateSettingsAsync(
            request.EnableEntraId.Value,
            request.TenantId,
            request.ClientId,
            ct));
    }

    private void SetCommonHeaders(double requestCharge = 1.0)
    {
        Response.Headers[CosmosHeaders.RequestCharge] = requestCharge.ToString("F2", CultureInfo.InvariantCulture);
        Response.Headers[CosmosHeaders.ActivityId] = Guid.NewGuid().ToString();
        Response.Headers[CosmosHeaders.ServiceVersion] = CosmosHeaders.CurrentServiceVersion;
        Response.Headers[CosmosHeaders.SchemaVersion] = CosmosHeaders.CurrentSchemaVersion;
    }
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
