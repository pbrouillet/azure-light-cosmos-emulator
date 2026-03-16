using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

/// <summary>
/// Cosmos DB REST API controller for database operations.
/// </summary>
[ApiController]
[Route("dbs")]
public class DatabasesController : CosmosControllerBase
{
    private readonly IDocumentStore _store;

    public DatabasesController(IDocumentStore store, CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
        _store = store;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var id = body["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
            return BadRequest(ErrorResponse("BadRequest", "Missing 'id' property."));

        try
        {
            var db = await _store.CreateDatabaseAsync(id, ct);

            if (body["maxThroughput"]?.GetValue<int>() is int maxThroughput)
                db.MaxThroughput = maxThroughput;

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = RuCostCalculator.CreateDatabase() }, ct);
            return StatusCode((int)HttpStatusCode.Created, FormatDatabase(db));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return Conflict(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _store.ListDatabasesAsync(ct);
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = RuCostCalculator.ListDatabases() }, ct);
        return Ok(new
        {
            _rid = "",
            Databases = result.Resources.Select(FormatDatabase),
            _count = result.Count
        });
    }

    [HttpGet("{dbId}")]
    public async Task<IActionResult> Get(string dbId, CancellationToken ct)
    {
        try
        {
            var db = await _store.GetDatabaseAsync(dbId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = RuCostCalculator.GetDatabase() }, ct);
            return Ok(FormatDatabase(db));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpDelete("{dbId}")]
    public async Task<IActionResult> Delete(string dbId, CancellationToken ct)
    {
        try
        {
            await _store.DeleteDatabaseAsync(dbId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = RuCostCalculator.DeleteDatabase() }, ct);
            return NoContent();
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
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

    private static object FormatDatabase(CosmosDatabase db) => new
    {
        id = db.Id,
        _rid = db.Rid,
        _self = db.Self,
        _etag = db.ETag,
        _ts = db.Timestamp,
        _colls = db.Colls,
        _users = db.Users,
        maxThroughput = db.MaxThroughput
    };

    private static object ErrorResponse(string code, string message) => new
    {
        code,
        message
    };
}
