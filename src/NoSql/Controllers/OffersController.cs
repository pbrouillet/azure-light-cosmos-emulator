using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

[ApiController]
[Route("offers")]
public class OffersController : CosmosControllerBase
{
    private readonly IDocumentStore _store;

    public OffersController(IDocumentStore store, CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
        _store = store;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _store.ListOffersAsync(ct);
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0 }, ct);
        return Ok(new { _rid = "", Offers = result.Resources.Select(FormatOffer), _count = result.Count });
    }

    [HttpGet("{offerId}")]
    public async Task<IActionResult> Get(string offerId, CancellationToken ct)
    {
        try
        {
            var offer = await _store.GetOfferAsync(offerId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0 }, ct);
            return Ok(FormatOffer(offer));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpPut("{offerId}")]
    public async Task<IActionResult> Replace(string offerId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        try
        {
            var existing = await _store.GetOfferAsync(offerId, ct);
            if (body["content"] is JsonObject contentObj && contentObj["offerThroughput"]?.GetValue<int>() is int throughput)
            {
                existing.Content.OfferThroughput = throughput;
            }

            var result = await _store.ReplaceOfferAsync(existing, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0 }, ct);
            return Ok(FormatOffer(result));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpPost]
    public async Task<IActionResult> Query(CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var result = await _store.ListOffersAsync(ct);
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0 }, ct);
        return Ok(new { _rid = "", Offers = result.Resources.Select(FormatOffer), _count = result.Count });
    }

    private async Task<JsonObject> ReadRequestBodyAsync(CancellationToken ct)
    {
        try
        {
            return await JsonNode.ParseAsync(Request.Body, cancellationToken: ct) as JsonObject
                ?? throw CosmosEmulatorException.BadRequest("Request body must be a JSON object.");
        }
        catch (JsonException)
        {
            throw CosmosEmulatorException.BadRequest("Request body must be valid JSON.");
        }
    }

    private static object FormatOffer(CosmosOffer o) => new
    {
        offerVersion = o.OfferVersion, offerType = o.OfferType,
        content = new { offerThroughput = o.Content.OfferThroughput },
        resource = o.Resource, offerResourceId = o.OfferResourceId,
        id = o.Id, _rid = o.Rid, _self = o.Self, _etag = o.ETag, _ts = o.Timestamp
    };

    private static object ErrorResponse(string code, string message) => new { code, message };
}
