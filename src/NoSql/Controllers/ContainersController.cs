using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Azure.Cosmos.LightEmulator.NoSql.Controllers;

/// <summary>
/// Cosmos DB REST API controller for container (collection) operations.
/// </summary>
[ApiController]
[Route("dbs/{dbId}/colls")]
public class ContainersController : CosmosControllerBase
{
    private static readonly JsonSerializerOptions IndexingPolicySerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IDocumentStore _store;

    public ContainersController(IDocumentStore store, CosmosResponseHeaderService responseHeaders)
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

        var partitionKeyNode = body["partitionKey"];
        if (partitionKeyNode is null)
            return BadRequest(ErrorResponse("BadRequest", "Missing 'partitionKey' property."));

        var pkDef = DeserializePartitionKey(partitionKeyNode);

        var container = new CosmosContainer
        {
            Id = id,
            DatabaseId = dbId,
            PartitionKey = pkDef
        };

        // Parse optional properties
        if (body["indexingPolicy"] is JsonObject indexNode)
            container.IndexingPolicy = JsonSerializer.Deserialize<IndexingPolicy>(indexNode, IndexingPolicySerializerOptions) ?? new IndexingPolicy();

        if (body["defaultTtl"]?.GetValue<int>() is int ttl)
            container.DefaultTimeToLive = ttl;

        if (body["maxThroughput"]?.GetValue<int>() is int maxThroughput)
            container.MaxThroughput = maxThroughput;

        try
        {
            var result = await _store.CreateContainerAsync(dbId, container, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.CreateContainer(),
                DatabaseId = dbId,
                ContainerId = result.Id,
                IncludeSessionToken = true
            }, ct);
            return StatusCode((int)HttpStatusCode.Created, FormatContainer(result));
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

    [HttpGet]
    public async Task<IActionResult> List(string dbId, CancellationToken ct)
    {
        try
        {
            var result = await _store.ListContainersAsync(dbId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = RuCostCalculator.ListContainers() }, ct);
            return Ok(new
            {
                _rid = "",
                DocumentCollections = result.Resources.Select(FormatContainer),
                _count = result.Count
            });
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpGet("{collId}")]
    public async Task<IActionResult> Get(string dbId, string collId, CancellationToken ct)
    {
        try
        {
            var container = await _store.GetContainerAsync(dbId, collId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions { RequestCharge = RuCostCalculator.GetContainer() }, ct);
            return Ok(FormatContainer(container));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpPut("{collId}")]
    public async Task<IActionResult> Replace(string dbId, string collId, CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);

        try
        {
            var existing = await _store.GetContainerAsync(dbId, collId, ct);

            if (body["indexingPolicy"] is JsonObject indexNode)
                existing.IndexingPolicy = JsonSerializer.Deserialize<IndexingPolicy>(indexNode, IndexingPolicySerializerOptions) ?? existing.IndexingPolicy;

            if (body["defaultTtl"]?.GetValue<int>() is int ttl)
                existing.DefaultTimeToLive = ttl;

            if (body["maxThroughput"]?.GetValue<int>() is int maxThroughput)
                existing.MaxThroughput = maxThroughput;

            var result = await _store.ReplaceContainerAsync(dbId, existing, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = 5.0,
                DatabaseId = dbId,
                ContainerId = result.Id,
                IncludeSessionToken = true
            }, ct);
            return Ok(FormatContainer(result));
        }
        catch (CosmosEmulatorException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound(ErrorResponse(ex.ErrorCode, ex.Message));
        }
    }

    [HttpDelete("{collId}")]
    public async Task<IActionResult> Delete(string dbId, string collId, CancellationToken ct)
    {
        try
        {
            await _store.DeleteContainerAsync(dbId, collId, ct);
            await SetCommonHeadersAsync(new CosmosResponseHeaderOptions
            {
                RequestCharge = RuCostCalculator.DeleteContainer(),
                DatabaseId = dbId,
                ContainerId = collId,
                IncludeSessionToken = true
            }, ct);
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

    private static PartitionKeyDefinition DeserializePartitionKey(JsonNode node)
    {
        if (node is JsonObject pkObj)
        {
            var paths = pkObj["paths"]?.AsArray()
                .Select(p => p?.GetValue<string>() ?? "")
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList() ?? ["/id"];

            var kind = pkObj["kind"]?.GetValue<string>() switch
            {
                "MultiHash" => PartitionKeyKind.MultiHash,
                "Range" => PartitionKeyKind.Range,
                _ => PartitionKeyKind.Hash
            };

            var version = pkObj["version"]?.GetValue<int>() ?? 2;

            return new PartitionKeyDefinition { Paths = paths, Kind = kind, Version = version };
        }

        return new PartitionKeyDefinition { Paths = ["/id"] };
    }

    private static object FormatContainer(CosmosContainer c) => new
    {
        id = c.Id,
        _rid = c.Rid,
        _self = c.Self,
        _etag = c.ETag,
        _ts = c.Timestamp,
        _docs = "docs/",
        _sprocs = "sprocs/",
        _triggers = "triggers/",
        _udfs = "udfs/",
        _conflicts = "conflicts/",
        partitionKey = new
        {
            paths = c.PartitionKey.Paths,
            kind = c.PartitionKey.Kind.ToString(),
            version = c.PartitionKey.Version
        },
        indexingPolicy = c.IndexingPolicy,
        defaultTtl = c.DefaultTimeToLive,
        maxThroughput = c.MaxThroughput
    };

    private static object ErrorResponse(string code, string message) => new { code, message };
}
