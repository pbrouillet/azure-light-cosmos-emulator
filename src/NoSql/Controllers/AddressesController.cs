using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("addresses")]
public class AddressesController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AddressesController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public IActionResult GetAddresses()
    {
        var port = _configuration.GetValue<int>("Emulator:Port", 8081);
        var enableSsl = _configuration.GetValue<bool>("Emulator:EnableSsl", false);
        var scheme = enableSsl ? "https" : "http";
        var endpoint = $"{scheme}://localhost:{port}";

        Response.Headers[CosmosHeaders.RequestCharge] = "1.00";
        Response.Headers[CosmosHeaders.ActivityId] = Guid.NewGuid().ToString();
        Response.Headers[CosmosHeaders.ServiceVersion] = CosmosHeaders.CurrentServiceVersion;

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
