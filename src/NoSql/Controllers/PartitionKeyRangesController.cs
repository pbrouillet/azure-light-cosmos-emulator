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
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
        {
            RequestCharge = 1.0,
            DatabaseId = dbId,
            ContainerId = collId
        }, ct);
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
                    _self = $"dbs/{dbId}/colls/{collId}/pkranges/0/",
                    _etag = "\"00000000-0000-0000-0000-000000000000\"",
                    _ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    minInclusive = string.Empty,
                    maxExclusive = "FF",
                    ridPrefix = 0,
                    throughputFraction = 1,
                    status = "online",
                    parents = Array.Empty<string>()
                }
            },
            _count = 1
        });
    }
}
