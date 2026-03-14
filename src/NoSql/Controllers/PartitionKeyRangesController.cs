using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("dbs/{dbId}/colls/{collId}/pkranges")]
public class PartitionKeyRangesController : ControllerBase
{
    [HttpGet]
    public IActionResult List(string collId)
    {
        Response.Headers[CosmosHeaders.RequestCharge] = "1";
        Response.Headers[CosmosHeaders.ActivityId] = Guid.NewGuid().ToString();
        Response.Headers[CosmosHeaders.ItemCount] = "1";
        Response.Headers[CosmosHeaders.ServiceVersion] = CosmosHeaders.CurrentServiceVersion;

        return Ok(new
        {
            _rid = collId,
            PartitionKeyRanges = new[]
            {
                new
                {
                    id = "0",
                    _rid = "0",
                    minInclusive = string.Empty,
                    maxExclusive = "FF",
                    throughputFraction = 1,
                    status = "online"
                }
            },
            _count = 1
        });
    }
}
