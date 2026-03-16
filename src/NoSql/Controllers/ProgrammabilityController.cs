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
/// Cosmos DB REST API controller for stored procedures, triggers, and UDFs.
/// </summary>
[ApiController]
public class ProgrammabilityController : CosmosControllerBase
{
    private readonly IProgrammabilityEngine _engine;

    public ProgrammabilityController(IProgrammabilityEngine engine, CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
        _engine = engine;
    }

    // Stored Procedures

    [HttpPost("dbs/{dbId}/colls/{collId}/sprocs")]
    public async Task<IActionResult> CreateSproc(string dbId, string collId, [FromBody] JsonObject body, CancellationToken ct)
    {
        var id = body["id"]?.GetValue<string>();
        var sprocBody = body["body"]?.GetValue<string>();

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(sprocBody))
            return BadRequest(new { code = "BadRequest", message = "Missing 'id' or 'body' property." });

        var sproc = new StoredProcedure
        {
            Id = id,
            DatabaseId = dbId,
            ContainerId = collId,
            Body = sprocBody
        };

        try
        {
            var result = await _engine.CreateStoredProcedureAsync(dbId, collId, sproc, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return StatusCode((int)HttpStatusCode.Created, FormatSproc(result));
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    [HttpGet("dbs/{dbId}/colls/{collId}/sprocs")]
    public async Task<IActionResult> ListSprocs(string dbId, string collId, CancellationToken ct)
    {
        var result = await _engine.ListStoredProceduresAsync(dbId, collId, ct);
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0, DatabaseId = dbId, ContainerId = collId }, ct);
        return Ok(new
        {
            _rid = "",
            StoredProcedures = result.Resources.Select(FormatSproc),
            _count = result.Count
        });
    }

    [HttpPut("dbs/{dbId}/colls/{collId}/sprocs/{sprocId}")]
    public async Task<IActionResult> ReplaceSproc(string dbId, string collId, string sprocId, [FromBody] JsonObject body, CancellationToken ct)
    {
        var sprocBody = body["body"]?.GetValue<string>();

        if (string.IsNullOrEmpty(sprocBody))
            return BadRequest(new { code = "BadRequest", message = "Missing 'body' property." });

        var sproc = new StoredProcedure
        {
            Id = sprocId,
            DatabaseId = dbId,
            ContainerId = collId,
            Body = sprocBody
        };

        try
        {
            var result = await _engine.ReplaceStoredProcedureAsync(dbId, collId, sproc, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return Ok(FormatSproc(result));
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    [HttpPost("dbs/{dbId}/colls/{collId}/sprocs/{sprocId}")]
    public async Task<IActionResult> ExecuteSproc(string dbId, string collId, string sprocId, [FromBody] JsonArray args, CancellationToken ct)
    {
        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var pk = ParsePartitionKey(pkHeader);

        try
        {
            var argsArray = args.Select(a => (object?)a).ToArray();
            var result = await _engine.ExecuteStoredProcedureAsync(dbId, collId, sprocId, argsArray, pk, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return Ok(result);
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    [HttpDelete("dbs/{dbId}/colls/{collId}/sprocs/{sprocId}")]
    public async Task<IActionResult> DeleteSproc(string dbId, string collId, string sprocId, CancellationToken ct)
    {
        try
        {
            await _engine.DeleteStoredProcedureAsync(dbId, collId, sprocId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return NoContent();
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    // Triggers

    [HttpPost("dbs/{dbId}/colls/{collId}/triggers")]
    public async Task<IActionResult> CreateTrigger(string dbId, string collId, [FromBody] JsonObject body, CancellationToken ct)
    {
        var id = body["id"]?.GetValue<string>();
        var triggerBody = body["body"]?.GetValue<string>();
        var triggerType = body["triggerType"]?.GetValue<string>();
        var triggerOperation = body["triggerOperation"]?.GetValue<string>();

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(triggerBody))
            return BadRequest(new { code = "BadRequest", message = "Missing 'id' or 'body' property." });

        var trigger = new Trigger
        {
            Id = id,
            DatabaseId = dbId,
            ContainerId = collId,
            Body = triggerBody,
            TriggerType = Enum.TryParse<TriggerType>(triggerType, true, out var tt) ? tt : TriggerType.Pre,
            TriggerOperation = Enum.TryParse<TriggerOperation>(triggerOperation, true, out var to) ? to : TriggerOperation.All
        };

        try
        {
            var result = await _engine.CreateTriggerAsync(dbId, collId, trigger, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return StatusCode((int)HttpStatusCode.Created, FormatTrigger(result));
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    [HttpGet("dbs/{dbId}/colls/{collId}/triggers")]
    public async Task<IActionResult> ListTriggers(string dbId, string collId, CancellationToken ct)
    {
        var result = await _engine.ListTriggersAsync(dbId, collId, ct);
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0, DatabaseId = dbId, ContainerId = collId }, ct);
        return Ok(new
        {
            _rid = "",
            Triggers = result.Resources.Select(FormatTrigger),
            _count = result.Count
        });
    }

    [HttpPut("dbs/{dbId}/colls/{collId}/triggers/{triggerId}")]
    public async Task<IActionResult> ReplaceTrigger(string dbId, string collId, string triggerId, [FromBody] JsonObject body, CancellationToken ct)
    {
        var triggerBody = body["body"]?.GetValue<string>();
        var triggerType = body["triggerType"]?.GetValue<string>();
        var triggerOperation = body["triggerOperation"]?.GetValue<string>();

        if (string.IsNullOrEmpty(triggerBody))
            return BadRequest(new { code = "BadRequest", message = "Missing 'body' property." });

        var trigger = new Trigger
        {
            Id = triggerId,
            DatabaseId = dbId,
            ContainerId = collId,
            Body = triggerBody,
            TriggerType = Enum.TryParse<TriggerType>(triggerType, true, out var tt) ? tt : TriggerType.Pre,
            TriggerOperation = Enum.TryParse<TriggerOperation>(triggerOperation, true, out var to) ? to : TriggerOperation.All
        };

        try
        {
            var result = await _engine.ReplaceTriggerAsync(dbId, collId, trigger, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return Ok(FormatTrigger(result));
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    [HttpDelete("dbs/{dbId}/colls/{collId}/triggers/{triggerId}")]
    public async Task<IActionResult> DeleteTrigger(string dbId, string collId, string triggerId, CancellationToken ct)
    {
        try
        {
            await _engine.DeleteTriggerAsync(dbId, collId, triggerId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return NoContent();
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    // User-Defined Functions

    [HttpPost("dbs/{dbId}/colls/{collId}/udfs")]
    public async Task<IActionResult> CreateUdf(string dbId, string collId, [FromBody] JsonObject body, CancellationToken ct)
    {
        var id = body["id"]?.GetValue<string>();
        var udfBody = body["body"]?.GetValue<string>();

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(udfBody))
            return BadRequest(new { code = "BadRequest", message = "Missing 'id' or 'body' property." });

        var udf = new UserDefinedFunction
        {
            Id = id,
            DatabaseId = dbId,
            ContainerId = collId,
            Body = udfBody
        };

        try
        {
            var result = await _engine.CreateUdfAsync(dbId, collId, udf, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return StatusCode((int)HttpStatusCode.Created, FormatUdf(result));
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    [HttpGet("dbs/{dbId}/colls/{collId}/udfs")]
    public async Task<IActionResult> ListUdfs(string dbId, string collId, CancellationToken ct)
    {
        var result = await _engine.ListUdfsAsync(dbId, collId, ct);
        await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0, DatabaseId = dbId, ContainerId = collId }, ct);
        return Ok(new
        {
            _rid = "",
            UserDefinedFunctions = result.Resources.Select(FormatUdf),
            _count = result.Count
        });
    }

    [HttpPut("dbs/{dbId}/colls/{collId}/udfs/{udfId}")]
    public async Task<IActionResult> ReplaceUdf(string dbId, string collId, string udfId, [FromBody] JsonObject body, CancellationToken ct)
    {
        var udfBody = body["body"]?.GetValue<string>();

        if (string.IsNullOrEmpty(udfBody))
            return BadRequest(new { code = "BadRequest", message = "Missing 'body' property." });

        var udf = new UserDefinedFunction
        {
            Id = udfId,
            DatabaseId = dbId,
            ContainerId = collId,
            Body = udfBody
        };

        try
        {
            var result = await _engine.ReplaceUdfAsync(dbId, collId, udf, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 1.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return Ok(FormatUdf(result));
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    [HttpDelete("dbs/{dbId}/colls/{collId}/udfs/{udfId}")]
    public async Task<IActionResult> DeleteUdf(string dbId, string collId, string udfId, CancellationToken ct)
    {
        try
        {
            await _engine.DeleteUdfAsync(dbId, collId, udfId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = 5.0, DatabaseId = dbId, ContainerId = collId, IncludeSessionToken = true }, ct);
            return NoContent();
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, new { code = ex.ErrorCode, message = ex.Message });
        }
    }

    // Helpers

    private static PartitionKeyValue ParsePartitionKey(string? header)
    {
        if (string.IsNullOrEmpty(header)) return PartitionKeyValue.Undefined;
        try
        {
            var array = JsonSerializer.Deserialize<JsonArray>(header);
            if (array is null || array.Count == 0) return PartitionKeyValue.Undefined;
            var values = array.Select<JsonNode?, object?>(n => n switch
            {
                null => null,
                _ when n.GetValueKind() == JsonValueKind.String => n.GetValue<string>(),
                _ when n.GetValueKind() == JsonValueKind.Number => n.GetValue<double>(),
                _ => n.ToJsonString()
            }).ToArray();
            return PartitionKeyValue.Create(values);
        }
        catch { return PartitionKeyValue.Undefined; }
    }

    private static object FormatSproc(StoredProcedure s) => new
    {
        id = s.Id, _rid = s.Rid, _self = s.Self, _etag = s.ETag, _ts = s.Timestamp, body = s.Body
    };

    private static object FormatTrigger(Trigger t) => new
    {
        id = t.Id, _rid = t.Rid, _self = t.Self, _etag = t.ETag, _ts = t.Timestamp,
        body = t.Body, triggerType = t.TriggerType.ToString(), triggerOperation = t.TriggerOperation.ToString()
    };

    private static object FormatUdf(UserDefinedFunction u) => new
    {
        id = u.Id, _rid = u.Rid, _self = u.Self, _etag = u.ETag, _ts = u.Timestamp, body = u.Body
    };
}
