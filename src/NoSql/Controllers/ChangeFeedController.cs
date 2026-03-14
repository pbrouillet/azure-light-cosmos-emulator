using System.Globalization;
using System.Net;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

/// <summary>
/// Cosmos DB REST API controller for change feed operations.
/// </summary>
[ApiController]
[Route("dbs/{dbId}/colls/{collId}/docs")]
public class ChangeFeedController : ControllerBase
{
    private readonly IChangeFeedProvider _changeFeed;

    public ChangeFeedController(IChangeFeedProvider changeFeed)
    {
        _changeFeed = changeFeed;
    }

    /// <summary>
    /// Reads the change feed. Activated by the A-IM: Incremental feed header.
    /// </summary>
    [HttpGet("changefeed")]
    public async Task<IActionResult> ReadChangeFeed(string dbId, string collId, CancellationToken ct)
    {
        var aim = Request.Headers[CosmosHeaders.IncrementalFeed].FirstOrDefault();
        if (!string.Equals(aim, CosmosHeaders.IncrementalFeedValue, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "BadRequest", message = "Missing A-IM: Incremental feed header." });

        var continuation = Request.Headers[CosmosHeaders.Continuation].FirstOrDefault()
                           ?? Request.Headers[CosmosHeaders.IfNoneMatch].FirstOrDefault();

        var maxItemCount = Request.Headers[CosmosHeaders.MaxItemCount].FirstOrDefault();

        var options = new ChangeFeedOptions
        {
            ContinuationToken = continuation,
            StartFromBeginning = string.IsNullOrEmpty(continuation),
            MaxItemCount = int.TryParse(maxItemCount, out var mic) ? mic : null
        };

        var result = await _changeFeed.ReadChangeFeedAsync(dbId, collId, options, ct);

        Response.Headers[CosmosHeaders.RequestCharge] = 1.0.ToString("F2", CultureInfo.InvariantCulture);
        Response.Headers[CosmosHeaders.ActivityId] = result.ActivityId;
        Response.Headers[CosmosHeaders.ItemCount] = result.Count.ToString();

        if (result.ContinuationToken != null)
        {
            Response.Headers[CosmosHeaders.Continuation] = result.ContinuationToken;
            Response.Headers.ETag = result.ContinuationToken;
        }

        if (result.Count == 0)
            return StatusCode(304); // Not Modified — no new changes

        return Ok(new
        {
            Documents = result.Resources.Select(item => item.Document.ToResponseBody()),
            _count = result.Count
        });
    }
}
