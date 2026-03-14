using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

/// <summary>
/// Cosmos DB REST API controller for document operations.
/// </summary>
[ApiController]
[Route("dbs/{dbId}/colls/{collId}/docs")]
public class DocumentsController : ControllerBase
{
    private const string RequestBodyLengthItemKey = "DocumentsController.RequestBodyLength";

    private readonly IDocumentStore _store;
    private readonly IQueryEngine _queryEngine;

    public DocumentsController(IDocumentStore store, IQueryEngine queryEngine)
    {
        _store = store;
        _queryEngine = queryEngine;
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrQuery(string dbId, string collId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var isQuery = Request.Headers[CosmosHeaders.IsQuery].FirstOrDefault();
        if (string.Equals(isQuery, "true", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteQuery(dbId, collId, body, ct);
        }

        var isUpsert = Request.Headers[CosmosHeaders.IsUpsert].FirstOrDefault();
        if (string.Equals(isUpsert, "true", StringComparison.OrdinalIgnoreCase))
        {
            return await Upsert(dbId, collId, body, ct);
        }

        return await Create(dbId, collId, body, ct);
    }

    [HttpGet("{docId}")]
    public async Task<IActionResult> Read(string dbId, string collId, string docId, CancellationToken ct)
    {
        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var partitionKey = ParsePartitionKey(pkHeader);

        try
        {
            var doc = await _store.ReadDocumentAsync(dbId, collId, docId, partitionKey, ct);
            SetCommonHeaders(RuCostCalculator.PointRead(doc.Body.ToJsonString().Length), doc);
            Response.Headers.ETag = doc.ETag;
            return Ok(doc.ToResponseBody());
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpPut("{docId}")]
    public async Task<IActionResult> Replace(string dbId, string collId, string docId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var ifMatch = Request.Headers[CosmosHeaders.IfMatch].FirstOrDefault();

        try
        {
            var doc = await _store.ReplaceDocumentAsync(dbId, collId, docId, body, ifMatch, ct);
            SetCommonHeaders(RuCostCalculator.Replace(body.ToJsonString().Length), doc);
            Response.Headers.ETag = doc.ETag;
            return Ok(doc.ToResponseBody());
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return StatusCode(412, ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpDelete("{docId}")]
    public async Task<IActionResult> Delete(string dbId, string collId, string docId, CancellationToken ct)
    {
        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var partitionKey = ParsePartitionKey(pkHeader);

        try
        {
            await _store.DeleteDocumentAsync(dbId, collId, docId, partitionKey, ct);
            SetCommonHeaders(RuCostCalculator.Delete());
            return NoContent();
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    private async Task<IActionResult> Create(string dbId, string collId, JsonObject body, CancellationToken ct)
    {
        try
        {
            var requestBodyLength = GetRequestBodyLength(body);
            var doc = await _store.CreateDocumentAsync(dbId, collId, body, ct);
            SetCommonHeaders(RuCostCalculator.Create(requestBodyLength), doc);
            Response.Headers.ETag = doc.ETag;
            return StatusCode((int)HttpStatusCode.Created, doc.ToResponseBody());
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return Conflict(ErrorResponse(ex.ErrorCode, ex.Message));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    private async Task<IActionResult> Upsert(string dbId, string collId, JsonObject body, CancellationToken ct)
    {
        try
        {
            var doc = await _store.UpsertDocumentAsync(dbId, collId, body, ct);
            SetCommonHeaders(RuCostCalculator.Upsert(body.ToJsonString().Length), doc);
            Response.Headers.ETag = doc.ETag;
            return Ok(doc.ToResponseBody());
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    private async Task<IActionResult> ExecuteQuery(string dbId, string collId, JsonObject body, CancellationToken ct)
    {
        var queryText = body["query"]?.GetValue<string>();
        if (string.IsNullOrEmpty(queryText))
            return BadRequest(ErrorResponse("BadRequest", "Missing 'query' property."));

        var parameters = new Dictionary<string, object?>();
        if (body["parameters"] is JsonArray paramsArray)
        {
            foreach (var param in paramsArray)
            {
                if (param is JsonObject paramObj)
                {
                    var name = paramObj["name"]?.GetValue<string>();
                    var value = paramObj["value"];
                    if (name != null)
                        parameters[name] = value;
                }
            }
        }

        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var enableCrossPartition = Request.Headers[CosmosHeaders.EnableCrossPartition].FirstOrDefault();
        var maxItemCount = Request.Headers[CosmosHeaders.MaxItemCount].FirstOrDefault();
        var continuation = Request.Headers[CosmosHeaders.Continuation].FirstOrDefault();

        var options = new QueryOptions
        {
            PartitionKey = !string.IsNullOrEmpty(pkHeader) ? ParsePartitionKey(pkHeader) : null,
            EnableCrossPartitionQuery = string.Equals(enableCrossPartition, "true", StringComparison.OrdinalIgnoreCase),
            MaxItemCount = int.TryParse(maxItemCount, out var mic) ? mic : null,
            ContinuationToken = continuation
        };

        try
        {
            var result = await _queryEngine.ExecuteQueryAsync(dbId, collId, queryText, parameters, options, ct);
            var totalSize = result.Resources.Sum(document => document?.ToJsonString().Length ?? 0);
            var isCrossPartition = string.Equals(
                Request.Headers[CosmosHeaders.EnableCrossPartition].FirstOrDefault(),
                "true",
                StringComparison.OrdinalIgnoreCase);
            SetCommonHeaders(RuCostCalculator.Query(result.Count, totalSize, isCrossPartition));
            Response.Headers[CosmosHeaders.ItemCount] = result.Count.ToString();
            if (result.ContinuationToken != null)
                Response.Headers[CosmosHeaders.Continuation] = result.ContinuationToken;

            return Ok(new
            {
                _rid = result.Rid,
                Documents = result.Resources,
                _count = result.Count
            });
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            return BadRequest(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    private async Task<JsonObject> ReadRequestBodyAsync(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var requestBody = await reader.ReadToEndAsync(ct);
            HttpContext.Items[RequestBodyLengthItemKey] = requestBody.Length;

            return JsonNode.Parse(requestBody) as JsonObject
                ?? throw CosmosEmulatorException.BadRequest("Request body must be a JSON object.");
        }
        catch (JsonException)
        {
            throw CosmosEmulatorException.BadRequest("Request body must be valid JSON.");
        }
    }

    private int GetRequestBodyLength(JsonObject body) =>
        HttpContext.Items.TryGetValue(RequestBodyLengthItemKey, out var requestBodyLength)
        && requestBodyLength is int length
            ? length
            : body.ToJsonString().Length;

    private void SetCommonHeaders(double ru = 1.0, CosmosDocument? doc = null)
    {
        Response.Headers[CosmosHeaders.RequestCharge] = ru.ToString("F2", CultureInfo.InvariantCulture);
        Response.Headers[CosmosHeaders.ActivityId] = Guid.NewGuid().ToString();
        Response.Headers[CosmosHeaders.ServiceVersion] = CosmosHeaders.CurrentServiceVersion;

        if (doc != null)
        {
            Response.Headers[CosmosHeaders.CosmosItemLsn] = doc.Lsn.ToString();
        }
    }

    private static PartitionKeyValue ParsePartitionKey(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return PartitionKeyValue.Undefined;

        try
        {
            var array = JsonSerializer.Deserialize<JsonArray>(header);
            if (array is null || array.Count == 0)
                return PartitionKeyValue.Undefined;

            var values = array.Select<JsonNode?, object?>(node => node switch
            {
                null => null,
                _ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
                _ when node.GetValueKind() == JsonValueKind.Number => node.GetValue<double>(),
                _ when node.GetValueKind() == JsonValueKind.True => true,
                _ when node.GetValueKind() == JsonValueKind.False => false,
                _ => node.ToJsonString()
            }).ToArray();

            return PartitionKeyValue.Create(values);
        }
        catch
        {
            return PartitionKeyValue.Undefined;
        }
    }

    private static object ErrorResponse(string code, string message) => new { code, message };
}
