using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("dbs/{dbId}/colls/{collId}/pkranges")]
public class PartitionKeyRangesController(CosmosResponseHeaderService responseHeaders) : CosmosControllerBase(responseHeaders)
{
    [HttpGet]
    public async Task<IActionResult> List(string dbId, string collId, CancellationToken ct)
    {
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { DatabaseId = dbId, ContainerId = collId }, ct);
        Response.Headers[CosmosHeaders.ItemCount] = "1";

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
