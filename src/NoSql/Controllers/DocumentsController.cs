using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Azure.Cosmos.LightEmulator.NoSql.Middleware;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.Triggers.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

/// <summary>
/// Cosmos DB REST API controller for document operations.
/// </summary>
[ApiController]
[Route("dbs/{dbId}/colls/{collId}/docs")]
public class DocumentsController : CosmosControllerBase
{
    private const string RequestBodyLengthItemKey = "DocumentsController.RequestBodyLength";
    private static readonly JsonSerializerOptions s_jsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private readonly IDocumentStore _store;
    private readonly IQueryEngine _queryEngine;
    private readonly TriggerEngine _triggerEngine;
    private readonly IQueryTelemetryStore _telemetryStore;
    private readonly IConsistencyManager _consistencyManager;
    private readonly QueryExplainService _queryExplainService;
    private readonly DmlCommandService _dmlCommandService;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IDocumentStore store,
        IQueryEngine queryEngine,
        TriggerEngine triggerEngine,
        IQueryTelemetryStore telemetryStore,
        IConsistencyManager consistencyManager,
        QueryExplainService queryExplainService,
        DmlCommandService dmlCommandService,
        CosmosResponseHeaderService responseHeaders,
        ILogger<DocumentsController> logger)
        : base(responseHeaders)
    {
        _store = store;
        _queryEngine = queryEngine;
        _triggerEngine = triggerEngine;
        _telemetryStore = telemetryStore;
        _consistencyManager = consistencyManager;
        _logger = logger;
        _queryExplainService = queryExplainService;
        _dmlCommandService = dmlCommandService;
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
        var pkError = RequirePartitionKeyHeader();
        if (pkError is not null) return pkError;

        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var partitionKey = ParsePartitionKey(pkHeader);

        try
        {
            var doc = await _store.ReadDocumentAsync(dbId, collId, docId, partitionKey, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.PointRead(doc.Body.ToJsonString().Length),
                DatabaseId = dbId,
                ContainerId = collId,
                ItemLsn = doc.Lsn
            }, ct);
            Response.Headers.ETag = doc.ETag;
            return Ok(doc.ToResponseBody());
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, await EnrichNotFoundMessageAsync(dbId, collId, docId, partitionKey, ex.Message, ct)));
        }
    }

    [HttpPut("{docId}")]
    public async Task<IActionResult> Replace(string dbId, string collId, string docId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        var ifMatch = Request.Headers[CosmosHeaders.IfMatch].FirstOrDefault();
        var isIndexed = ParseIndexingDirective();

        var preTriggers = ParseTriggerHeader(CosmosHeaders.PreTriggerInclude);
        var postTriggers = ParseTriggerHeader(CosmosHeaders.PostTriggerInclude);

        try
        {
            if (preTriggers.Length > 0)
                body = await _triggerEngine.ExecutePreTriggersAsync(dbId, collId, body, TriggerOperation.Replace, preTriggers, ct);

            var doc = await _store.ReplaceDocumentAsync(dbId, collId, docId, body, ifMatch, isIndexed, ct);

            if (postTriggers.Length > 0)
                await _triggerEngine.ExecutePostTriggersAsync(dbId, collId, doc, TriggerOperation.Replace, postTriggers, ct);

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.Replace(body.ToJsonString().Length),
                DatabaseId = dbId,
                ContainerId = collId,
                ItemLsn = doc.Lsn,
                IncludeSessionToken = true,
                SessionLsn = doc.Lsn
            }, ct);
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
        var pkError = RequirePartitionKeyHeader();
        if (pkError is not null) return pkError;

        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var partitionKey = ParsePartitionKey(pkHeader);

        _logger.LogInformation("DELETE doc {DocId} in {DbId}/{CollId} — PK header: {PkHeader}, parsed: {ParsedPk}",
            docId, dbId, collId, pkHeader, partitionKey.ToHeaderString());

        var preTriggers = ParseTriggerHeader(CosmosHeaders.PreTriggerInclude);
        var postTriggers = ParseTriggerHeader(CosmosHeaders.PostTriggerInclude);

        try
        {
            CosmosDocument? existingDoc = null;
            if (preTriggers.Length > 0 || postTriggers.Length > 0)
                existingDoc = await _store.ReadDocumentAsync(dbId, collId, docId, partitionKey, ct);

            if (preTriggers.Length > 0)
                await _triggerEngine.ExecutePreTriggersAsync(dbId, collId, existingDoc!.ToResponseBody(), TriggerOperation.Delete, preTriggers, ct);

            await _store.DeleteDocumentAsync(dbId, collId, docId, partitionKey, ct);

            if (postTriggers.Length > 0)
                await _triggerEngine.ExecutePostTriggersAsync(dbId, collId, existingDoc!, TriggerOperation.Delete, postTriggers, ct);

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.Delete(),
                DatabaseId = dbId,
                ContainerId = collId,
                IncludeSessionToken = true,
                SessionLsn = await _store.GetGlobalLsnAsync(ct)
            }, ct);
            return NoContent();
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var enrichedMessage = await EnrichNotFoundMessageAsync(dbId, collId, docId, partitionKey, ex.Message, ct);
            _logger.LogWarning("DELETE {DbId}/{CollId}/{DocId} → 404: {Message}", dbId, collId, docId, enrichedMessage);
            return NotFound(ErrorResponse(ex.ErrorCode, enrichedMessage));
        }
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteAll(string dbId, string collId, CancellationToken ct)
    {
        try
        {
            var deletedCount = await _store.EmptyContainerAsync(dbId, collId, ct);

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.Delete() * deletedCount,
                DatabaseId = dbId,
                ContainerId = collId,
                IncludeSessionToken = true,
                SessionLsn = await _store.GetGlobalLsnAsync(ct)
            }, ct);
            Response.Headers[CosmosHeaders.ItemCount] = deletedCount.ToString();
            return NoContent();
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpPatch("{docId}")]
    public async Task<IActionResult> Patch(string dbId, string collId, string docId, CancellationToken ct)
    {
        var pkError = RequirePartitionKeyHeader();
        if (pkError is not null) return pkError;

        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var partitionKey = ParsePartitionKey(pkHeader);
        var ifMatch = Request.Headers[CosmosHeaders.IfMatch].FirstOrDefault();

        var body = await ReadRequestBodyAsync(ct);
        var operationsNode = body["operations"] as JsonArray;
        if (operationsNode is null || operationsNode.Count == 0)
            return BadRequest(ErrorResponse("BadRequest", "PATCH request must include a non-empty 'operations' array."));

        var operations = new List<PatchOperation>();
        foreach (var opNode in operationsNode)
        {
            if (opNode is not JsonObject opObj)
                return BadRequest(ErrorResponse("BadRequest", "Each operation must be a JSON object."));

            var op = opObj["op"]?.GetValue<string>();
            var path = opObj["path"]?.GetValue<string>();
            if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(path))
                return BadRequest(ErrorResponse("BadRequest", "Each operation must have 'op' and 'path' properties."));

            operations.Add(new PatchOperation
            {
                Op = op,
                Path = path,
                Value = opObj.ContainsKey("value") ? opObj["value"] : null,
                From = opObj["from"]?.GetValue<string>()
            });
        }

        var preTriggers = ParseTriggerHeader(CosmosHeaders.PreTriggerInclude);
        var postTriggers = ParseTriggerHeader(CosmosHeaders.PostTriggerInclude);

        try
        {
            if (preTriggers.Length > 0)
            {
                var existingDoc = await _store.ReadDocumentAsync(dbId, collId, docId, partitionKey, ct);
                await _triggerEngine.ExecutePreTriggersAsync(dbId, collId, existingDoc.ToResponseBody(), TriggerOperation.Replace, preTriggers, ct);
            }

            var condition = body["condition"]?.GetValue<string>();
            var doc = await _store.PatchDocumentAsync(dbId, collId, docId, partitionKey, operations, ifMatch, condition, ct);

            if (postTriggers.Length > 0)
                await _triggerEngine.ExecutePostTriggersAsync(dbId, collId, doc, TriggerOperation.Replace, postTriggers, ct);

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.Replace(doc.Body.ToJsonString().Length),
                DatabaseId = dbId,
                ContainerId = collId,
                ItemLsn = doc.Lsn,
                IncludeSessionToken = true,
                SessionLsn = doc.Lsn
            }, ct);
            Response.Headers.ETag = doc.ETag;
            return Ok(doc.ToResponseBody());
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, await EnrichNotFoundMessageAsync(dbId, collId, docId, partitionKey, ex.Message, ct)));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return StatusCode(412, ErrorResponse(ex.ErrorCode, ex.Message));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            return BadRequest(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    private async Task<IActionResult> Create(string dbId, string collId, JsonObject body, CancellationToken ct)
    {
        try
        {
            var preTriggers = ParseTriggerHeader(CosmosHeaders.PreTriggerInclude);
            var postTriggers = ParseTriggerHeader(CosmosHeaders.PostTriggerInclude);

            if (preTriggers.Length > 0)
                body = await _triggerEngine.ExecutePreTriggersAsync(dbId, collId, body, TriggerOperation.Create, preTriggers, ct);

            var isIndexed = ParseIndexingDirective();
            var requestBodyLength = GetRequestBodyLength(body);
            var doc = await _store.CreateDocumentAsync(dbId, collId, body, isIndexed, ct);

            if (postTriggers.Length > 0)
                await _triggerEngine.ExecutePostTriggersAsync(dbId, collId, doc, TriggerOperation.Create, postTriggers, ct);

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.Create(requestBodyLength),
                DatabaseId = dbId,
                ContainerId = collId,
                ItemLsn = doc.Lsn,
                IncludeSessionToken = true,
                SessionLsn = doc.Lsn
            }, ct);
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
        var preTriggers = ParseTriggerHeader(CosmosHeaders.PreTriggerInclude);
        var postTriggers = ParseTriggerHeader(CosmosHeaders.PostTriggerInclude);

        try
        {
            if (preTriggers.Length > 0)
                body = await _triggerEngine.ExecutePreTriggersAsync(dbId, collId, body, TriggerOperation.Create, preTriggers, ct);

            var isIndexed = ParseIndexingDirective();
            var doc = await _store.UpsertDocumentAsync(dbId, collId, body, isIndexed, ct);

            if (postTriggers.Length > 0)
                await _triggerEngine.ExecutePostTriggersAsync(dbId, collId, doc, TriggerOperation.Create, postTriggers, ct);

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.Upsert(body.ToJsonString().Length),
                DatabaseId = dbId,
                ContainerId = collId,
                ItemLsn = doc.Lsn,
                IncludeSessionToken = true,
                SessionLsn = doc.Lsn
            }, ct);
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
        var enableScan = Request.Headers[CosmosHeaders.EnableScan].FirstOrDefault();

        // Route DML statements (INSERT/UPDATE/DELETE) to DmlCommandService
        if (DmlCommandService.IsDml(queryText))
        {
            try
            {
                var dmlResult = await _dmlCommandService.ExecuteAsync(dbId, collId, queryText, parameters, ct);
                var dmlCharge = dmlResult.Resources.Count * 5.0; // estimate ~5 RU per affected doc
                var dmlActivityId = Guid.NewGuid().ToString();

                await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
                {
                    RequestCharge = dmlCharge,
                    DatabaseId = dbId,
                    ContainerId = collId,
                    IncludeSessionToken = true,
                    ActivityId = dmlActivityId
                }, ct);
                Response.Headers[CosmosHeaders.ItemCount] = dmlResult.Resources.Count.ToString();

                return Ok(new
                {
                    _rid = dmlResult.Rid,
                    Documents = dmlResult.Resources,
                    _count = dmlResult.Resources.Count
                });
            }
            catch (CosmosEmulatorException ex) when (
                ex.StatusCode == HttpStatusCode.BadRequest ||
                ex.StatusCode == HttpStatusCode.NotFound ||
                ex.StatusCode == HttpStatusCode.Conflict)
            {
                return StatusCode((int)ex.StatusCode, ErrorResponse(ex.ErrorCode, ex.Message));
            }
        }

        // Read effective consistency from middleware
        var effectiveConsistency = HttpContext.Items.TryGetValue(ConsistencyMiddleware.EffectiveConsistencyKey, out var cl)
            && cl is ConsistencyLevel level
                ? level
                : _consistencyManager.DefaultConsistencyLevel;

        var isCrossPartition = string.Equals(enableCrossPartition, "true", StringComparison.OrdinalIgnoreCase);

        var options = new QueryOptions
        {
            PartitionKey = !string.IsNullOrEmpty(pkHeader) ? ParsePartitionKey(pkHeader) : null,
            EnableCrossPartitionQuery = isCrossPartition,
            MaxItemCount = int.TryParse(maxItemCount, out var mic) ? mic : null,
            ContinuationToken = continuation,
            EnableScan = string.Equals(enableScan, "true", StringComparison.OrdinalIgnoreCase),
            ConsistencyLevel = effectiveConsistency
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _queryEngine.ExecuteQueryAsync(dbId, collId, queryText, parameters, options, ct);
            sw.Stop();

            var totalSize = result.Resources.Sum(document => document?.ToJsonString().Length ?? 0);
            var requestCharge = RuCostCalculator.Query(result.Count, totalSize, isCrossPartition, scanMultiplier: result.RuMultiplier);
            var activityId = Guid.NewGuid().ToString();

            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = requestCharge,
                DatabaseId = dbId,
                ContainerId = collId,
                IncludeSessionToken = true,
                ActivityId = activityId
            }, ct);
            Response.Headers[CosmosHeaders.ItemCount] = result.Count.ToString();
            if (result.ContinuationToken != null)
                Response.Headers[CosmosHeaders.Continuation] = result.ContinuationToken;

            // Fire-and-forget telemetry recording with query plan
            _ = RecordQueryTelemetryAsync(new QueryTelemetryEntry
            {
                DatabaseId = dbId,
                ContainerId = collId,
                SqlText = queryText,
                PartitionKey = pkHeader,
                ConsistencyLevel = effectiveConsistency.ToString(),
                RequestCharge = requestCharge,
                LatencyMs = sw.ElapsedMilliseconds,
                ItemCount = result.Count,
                StatusCode = 200,
                ActivityId = activityId,
                ContinuationToken = result.ContinuationToken,
                IsCrossPartition = isCrossPartition
            });

            return Ok(new
            {
                _rid = result.Rid,
                Documents = result.IsValueProjection
                    ? result.Resources.Select(r => r["$1"]).ToList<object?>()
                    : (object)result.Resources,
                _count = result.Count
            });
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            sw.Stop();

            // Record failed query telemetry (no plan for failed queries)
            _ = _telemetryStore.RecordAsync(new QueryTelemetryEntry
            {
                DatabaseId = dbId,
                ContainerId = collId,
                SqlText = queryText,
                PartitionKey = pkHeader,
                ConsistencyLevel = effectiveConsistency.ToString(),
                RequestCharge = 0,
                LatencyMs = sw.ElapsedMilliseconds,
                ItemCount = 0,
                StatusCode = 400,
                ActivityId = Guid.NewGuid().ToString(),
                IsCrossPartition = isCrossPartition
            }, CancellationToken.None);

            return BadRequest(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    private async Task RecordQueryTelemetryAsync(QueryTelemetryEntry entry)
    {
        try
        {
            var explainResult = await _queryExplainService.ExplainAsync(
                entry.DatabaseId, entry.ContainerId, entry.SqlText, CancellationToken.None);
            entry.QueryPlan = JsonSerializer.Serialize(explainResult, s_jsonOptions);
        }
        catch
        {
            // Graceful degradation: record telemetry without a plan if explain fails
        }

        await _telemetryStore.RecordAsync(entry, CancellationToken.None);
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

    /// <summary>
    /// When a document is not found by ID + partition key, checks whether the document exists
    /// under a different partition key. Returns an enriched error message to help diagnose
    /// partition key mismatches (common when the Explorer or SDK sends the wrong PK value).
    /// </summary>
    private async Task<string> EnrichNotFoundMessageAsync(
        string dbId, string collId, string docId,
        PartitionKeyValue searchedPk, string originalMessage, CancellationToken ct)
    {
        try
        {
            var allDocs = await _store.ListDocumentsAsync(dbId, collId, ct);
            var totalCount = allDocs.Resources.Count;
            var matches = allDocs.Resources
                .Where(d => string.Equals(d.Id, docId, StringComparison.Ordinal))
                .ToList();

            _logger.LogWarning(
                "DELETE 404 for doc '{DocId}' with PK {SearchedPk} in {DbId}/{CollId}. " +
                "Container has {TotalDocs} document(s), {MatchCount} with this id.",
                docId, searchedPk.ToHeaderString(), dbId, collId, totalCount, matches.Count);

            // Log first few documents in the container for debugging
            foreach (var d in allDocs.Resources.Take(10))
            {
                _logger.LogWarning("  → stored doc id='{Id}', pk={Pk}",
                    d.Id, d.PartitionKey.ToHeaderString());
            }
            if (totalCount > 10)
                _logger.LogWarning("  → ... and {Remaining} more", totalCount - 10);

            if (matches.Count == 1)
            {
                return $"{originalMessage} The document exists with partition key " +
                       $"{matches[0].PartitionKey.ToHeaderString()} but the request specified " +
                       $"{searchedPk.ToHeaderString()}.";
            }
            if (matches.Count > 1)
            {
                var pks = string.Join(", ", matches.Select(d => d.PartitionKey.ToHeaderString()));
                return $"{originalMessage} The document id exists under {matches.Count} " +
                       $"partition keys ({pks}) but none matched the request value " +
                       $"{searchedPk.ToHeaderString()}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnrichNotFoundMessageAsync failed for {DbId}/{CollId}/{DocId}", dbId, collId, docId);
        }

        return originalMessage;
    }

    private IActionResult? RequirePartitionKeyHeader()
    {
        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        if (string.IsNullOrEmpty(pkHeader))
            return BadRequest(ErrorResponse("BadRequest", "PartitionKey value must be supplied for this operation."));

        var parsed = ParsePartitionKey(pkHeader);
        if (parsed.Components.Count == 0)
            return BadRequest(ErrorResponse("BadRequest",
                $"PartitionKey extracted from header is empty. Ensure the '{CosmosHeaders.PartitionKey}' header " +
                "is a valid JSON array with at least one element, e.g. [\"value\"]."));

        return null;
    }

    private string[] ParseTriggerHeader(string headerName)
    {
        var value = Request.Headers[headerName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private bool? ParseIndexingDirective()
    {
        var value = Request.Headers[CosmosHeaders.IndexingDirective].FirstOrDefault();
        return value?.ToUpperInvariant() switch
        {
            "INCLUDE" => true,
            "EXCLUDE" => false,
            _ => null
        };
    }
}
