using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("addresses")]
public class AddressesController : CosmosControllerBase
{
    private readonly IConfiguration _configuration;

    public AddressesController(IConfiguration configuration, CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> GetAddresses(CancellationToken ct)
    {
        var port = _configuration.GetValue<int>("Emulator:Port", 8081);
        var enableSsl = _configuration.GetValue<bool>("Emulator:EnableSsl", false);
        var scheme = enableSsl ? "https" : "http";
        var endpoint = $"{scheme}://localhost:{port}";

        await SetCommonHeadersAsync(ct: ct);
        return Ok(new
        {
            _count = 1,
            Addresses = new object[]
            {
                new
                {
                    id = "0",
                    partitionKeyRangeId = "0",
                    protocol = scheme,
                    logicalUri = $"rntbd://localhost:{port}/",
                    physicalUri = $"{endpoint}/",
                    isPrimary = true
                }
            }
        });
    }
}
