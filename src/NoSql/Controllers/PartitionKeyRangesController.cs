using System.Net;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("dbs/{dbId}/colls/{collId}/pkranges")]
public class PartitionKeyRangesController(CosmosResponseHeaderService responseHeaders) : CosmosControllerBase(responseHeaders)
{
    // The emulator never splits partitions, so the routing map is immutable and
    // its ETag is constant. SDK clients (Python/.NET v3/Java/Go) read /pkranges
    // as an incremental change feed and only stop draining on HTTP 304 Not
    // Modified. Returning 200 unconditionally makes those clients loop forever,
    // so we honor If-None-Match against this stable ETag and reply 304 once the
    // client has already seen the (single, static) range.
    private const string RoutingMapETag = "\"00000000-0000-0000-0000-000000000000\"";

    [HttpGet]
    public async Task<IActionResult> List(string dbId, string collId, CancellationToken ct)
    {
        var ifNoneMatch = Request.Headers[CosmosHeaders.IfNoneMatch].FirstOrDefault();
        if (string.Equals(ifNoneMatch, RoutingMapETag, StringComparison.Ordinal))
        {
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = 1.0,
                DatabaseId = dbId,
                ContainerId = collId
            }, ct);
            Response.Headers.ETag = RoutingMapETag;
            return StatusCode((int)HttpStatusCode.NotModified);
        }

        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
        {
            RequestCharge = 1.0,
            DatabaseId = dbId,
            ContainerId = collId
        }, ct);
        Response.Headers[CosmosHeaders.ItemCount] = "1";
        Response.Headers.ETag = RoutingMapETag;

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
