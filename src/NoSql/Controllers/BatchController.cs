using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

/// <summary>
/// Cosmos DB REST API controller for transactional batch operations.
/// </summary>
[ApiController]
[Route("dbs/{dbId}/colls/{collId}")]
public class BatchController : CosmosControllerBase
{
    private const int MaxBatchOperations = 100;
    private const int MaxBatchRequestSize = 2 * 1024 * 1024; // 2 MB

    private readonly IDocumentStore _store;

    public BatchController(IDocumentStore store, CosmosResponseHeaderService responseHeaders)
        : base(responseHeaders)
    {
        _store = store;
    }

    [HttpPost]
    public async Task<IActionResult> ExecuteBatch(string dbId, string collId, CancellationToken ct)
    {
        var isBatch = Request.Headers[CosmosHeaders.IsBatchRequest].FirstOrDefault();
        if (!string.Equals(isBatch, "true", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        // Parse partition key
        var pkHeader = Request.Headers[CosmosHeaders.PartitionKey].FirstOrDefault();
        var partitionKey = ParsePartitionKey(pkHeader);

        // Read and validate request body
        string requestBody;
        using (var reader = new StreamReader(Request.Body))
        {
            requestBody = await reader.ReadToEndAsync(ct);
        }

        if (requestBody.Length > MaxBatchRequestSize)
            return BadRequest(ErrorResponse("BadRequest",
                $"Request body size ({requestBody.Length} bytes) exceeds the maximum allowed size ({MaxBatchRequestSize} bytes)."));

        JsonArray? operationsArray;
        try
        {
            operationsArray = JsonNode.Parse(requestBody) as JsonArray;
        }
        catch (JsonException)
        {
            return BadRequest(ErrorResponse("BadRequest", "Request body must be a valid JSON array."));
        }

        if (operationsArray is null)
            return BadRequest(ErrorResponse("BadRequest", "Request body must be a JSON array of operations."));

        if (operationsArray.Count > MaxBatchOperations)
            return BadRequest(ErrorResponse("BadRequest",
                $"Batch request contains {operationsArray.Count} operations, which exceeds the maximum of {MaxBatchOperations}."));

        // Parse operations
        var operations = new List<BatchOperationRequest>();
        for (var i = 0; i < operationsArray.Count; i++)
        {
            if (operationsArray[i] is not JsonObject opObj)
                return BadRequest(ErrorResponse("BadRequest", $"Operation at index {i} must be a JSON object."));

            var opTypeStr = opObj["operationType"]?.GetValue<string>();
            if (string.IsNullOrEmpty(opTypeStr) || !Enum.TryParse<BatchOperationType>(opTypeStr, ignoreCase: true, out var opType))
                return BadRequest(ErrorResponse("BadRequest", $"Operation at index {i} has an invalid or missing 'operationType'."));

            var opRequest = new BatchOperationRequest { OperationType = opType };

            opRequest.Id = opObj["id"]?.GetValue<string>();
            opRequest.ResourceBody = opObj["resourceBody"] as JsonObject;
            opRequest.IfMatch = opObj["ifMatch"]?.GetValue<string>();
            opRequest.IfNoneMatch = opObj["ifNoneMatch"]?.GetValue<string>();

            // Validate required fields per operation type
            var validationError = ValidateOperation(i, opRequest);
            if (validationError is not null)
                return BadRequest(ErrorResponse("BadRequest", validationError));

            operations.Add(opRequest);
        }

        try
        {
            var results = await _store.ExecuteBatchAsync(dbId, collId, partitionKey, operations, ct);

            var totalCharge = results.Sum(r => r.RequestCharge);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = totalCharge,
                DatabaseId = dbId,
                ContainerId = collId,
                IncludeSessionToken = true,
                SessionLsn = await _store.GetGlobalLsnAsync(ct)
            }, ct);

            // Build response array
            var responseArray = new JsonArray();
            foreach (var result in results)
            {
                var resultObj = new JsonObject
                {
                    ["statusCode"] = result.StatusCode,
                    ["requestCharge"] = result.RequestCharge
                };

                if (result.ResourceBody is not null)
                    resultObj["resourceBody"] = result.ResourceBody.DeepClone();

                if (result.ETag is not null)
                    resultObj["eTag"] = result.ETag;

                if (result.RetryAfterMs is not null)
                    resultObj["retryAfterMs"] = result.RetryAfterMs;

                responseArray.Add(resultObj);
            }

            return Ok(responseArray);
        }
        catch (CosmosEmulatorException ex)
        {
            return StatusCode((int)ex.StatusCode, ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    private static string? ValidateOperation(int index, BatchOperationRequest op)
    {
        switch (op.OperationType)
        {
            case BatchOperationType.Create:
                if (op.ResourceBody is null)
                    return $"Operation at index {index} (Create) requires a 'resourceBody'.";
                if (op.ResourceBody["id"]?.GetValue<string>() is null)
                    return $"Operation at index {index} (Create) requires 'resourceBody' to have an 'id' property.";
                break;

            case BatchOperationType.Read:
                if (string.IsNullOrEmpty(op.Id))
                    return $"Operation at index {index} (Read) requires an 'id'.";
                break;

            case BatchOperationType.Replace:
                if (string.IsNullOrEmpty(op.Id))
                    return $"Operation at index {index} (Replace) requires an 'id'.";
                if (op.ResourceBody is null)
                    return $"Operation at index {index} (Replace) requires a 'resourceBody'.";
                break;

            case BatchOperationType.Upsert:
                if (op.ResourceBody is null)
                    return $"Operation at index {index} (Upsert) requires a 'resourceBody'.";
                if (op.ResourceBody["id"]?.GetValue<string>() is null)
                    return $"Operation at index {index} (Upsert) requires 'resourceBody' to have an 'id' property.";
                break;

            case BatchOperationType.Delete:
                if (string.IsNullOrEmpty(op.Id))
                    return $"Operation at index {index} (Delete) requires an 'id'.";
                break;

            case BatchOperationType.Patch:
                if (string.IsNullOrEmpty(op.Id))
                    return $"Operation at index {index} (Patch) requires an 'id'.";
                if (op.ResourceBody is null)
                    return $"Operation at index {index} (Patch) requires a 'resourceBody'.";
                if (op.ResourceBody["operations"] is not JsonArray)
                    return $"Operation at index {index} (Patch) requires 'resourceBody' to have an 'operations' array.";
                break;
        }

        return null;
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
