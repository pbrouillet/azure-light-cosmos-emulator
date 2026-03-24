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
[Route("dbs/{dbId}/users")]
public class UsersController : CosmosControllerBase
{
    private readonly IDocumentStore _store;

    public UsersController(IDocumentStore store, CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
        _store = store;
    }

    [HttpPost]
    public async Task<IActionResult> Create(string dbId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var id = body["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
            return BadRequest(ErrorResponse("BadRequest", "Missing 'id' property."));

        try
        {
            var user = await _store.CreateUserAsync(dbId, id, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0 }, ct);
            return StatusCode((int)HttpStatusCode.Created, FormatUser(user));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return StatusCode((int)ex.StatusCode, ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(string dbId, CancellationToken ct)
    {
        try
        {
            var result = await _store.ListUsersAsync(dbId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0 }, ct);
            return Ok(new { _rid = "", Users = result.Resources.Select(FormatUser), _count = result.Count });
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpGet("{userId}")]
    public async Task<IActionResult> Get(string dbId, string userId, CancellationToken ct)
    {
        try
        {
            var user = await _store.GetUserAsync(dbId, userId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0 }, ct);
            return Ok(FormatUser(user));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpPut("{userId}")]
    public async Task<IActionResult> Replace(string dbId, string userId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        try
        {
            var existing = await _store.GetUserAsync(dbId, userId, ct);
            var result = await _store.ReplaceUserAsync(dbId, existing, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0 }, ct);
            return Ok(FormatUser(result));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> Delete(string dbId, string userId, CancellationToken ct)
    {
        try
        {
            await _store.DeleteUserAsync(dbId, userId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0 }, ct);
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

    private static object FormatUser(CosmosUser u) => new
    {
        id = u.Id, _rid = u.Rid, _self = u.Self, _etag = u.ETag, _ts = u.Timestamp, _permissions = u.Permissions
    };

    private static object ErrorResponse(string code, string message) => new { code, message };
}
