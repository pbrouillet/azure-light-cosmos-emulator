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
[Route("dbs/{dbId}/users/{userId}/permissions")]
public class PermissionsController : CosmosControllerBase
{
    private readonly IDocumentStore _store;

    public PermissionsController(IDocumentStore store, CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
        _store = store;
    }

    [HttpPost]
    public async Task<IActionResult> Create(string dbId, string userId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var id = body["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
            return BadRequest(ErrorResponse("BadRequest", "Missing 'id' property."));

        var permissionModeStr = body["permissionMode"]?.GetValue<string>() ?? "All";
        var permissionMode = string.Equals(permissionModeStr, "Read", StringComparison.OrdinalIgnoreCase)
            ? PermissionMode.Read : PermissionMode.All;

        var resource = body["resource"]?.GetValue<string>();
        if (string.IsNullOrEmpty(resource))
            return BadRequest(ErrorResponse("BadRequest", "Missing 'resource' property."));

        try
        {
            var permission = new CosmosPermission
            {
                Id = id, DatabaseId = dbId, UserId = userId,
                PermissionMode = permissionMode, Resource = resource
            };
            var result = await _store.CreatePermissionAsync(dbId, userId, permission, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0 }, ct);
            return StatusCode((int)HttpStatusCode.Created, FormatPermission(result));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return StatusCode((int)ex.StatusCode, ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(string dbId, string userId, CancellationToken ct)
    {
        try
        {
            var result = await _store.ListPermissionsAsync(dbId, userId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0 }, ct);
            return Ok(new { _rid = "", Permissions = result.Resources.Select(FormatPermission), _count = result.Count });
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpGet("{permissionId}")]
    public async Task<IActionResult> Get(string dbId, string userId, string permissionId, CancellationToken ct)
    {
        try
        {
            var permission = await _store.GetPermissionAsync(dbId, userId, permissionId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0 }, ct);
            return Ok(FormatPermission(permission));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpPut("{permissionId}")]
    public async Task<IActionResult> Replace(string dbId, string userId, string permissionId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var permissionModeStr = body["permissionMode"]?.GetValue<string>() ?? "All";
        var permissionMode = string.Equals(permissionModeStr, "Read", StringComparison.OrdinalIgnoreCase)
            ? PermissionMode.Read : PermissionMode.All;
        var resource = body["resource"]?.GetValue<string>();

        try
        {
            var existing = await _store.GetPermissionAsync(dbId, userId, permissionId, ct);
            existing.PermissionMode = permissionMode;
            if (!string.IsNullOrEmpty(resource))
                existing.Resource = resource;

            var result = await _store.ReplacePermissionAsync(dbId, userId, existing, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0 }, ct);
            return Ok(FormatPermission(result));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpDelete("{permissionId}")]
    public async Task<IActionResult> Delete(string dbId, string userId, string permissionId, CancellationToken ct)
    {
        try
        {
            await _store.DeletePermissionAsync(dbId, userId, permissionId, ct);
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

    private static object FormatPermission(CosmosPermission p) => new
    {
        id = p.Id, _rid = p.Rid, _self = p.Self, _etag = p.ETag, _ts = p.Timestamp,
        permissionMode = p.PermissionMode.ToString(), resource = p.Resource, _token = p.Token
    };

    private static object ErrorResponse(string code, string message) => new { code, message };
}
