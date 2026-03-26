using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.NoSql.Middleware;

/// <summary>
/// Middleware that reads the x-ms-consistency-level header, validates it against
/// the account default, and stores the effective consistency in HttpContext.Items.
/// Also validates session tokens on read/query operations when session consistency is in effect.
/// </summary>
public class ConsistencyMiddleware
{
    /// <summary>Key used to store the effective consistency level in HttpContext.Items.</summary>
    public const string EffectiveConsistencyKey = "EffectiveConsistencyLevel";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private readonly RequestDelegate _next;
    private readonly IConsistencyManager _consistencyManager;
    private readonly ILogger<ConsistencyMiddleware> _logger;

    public ConsistencyMiddleware(
        RequestDelegate next,
        IConsistencyManager consistencyManager,
        ILogger<ConsistencyMiddleware> logger)
    {
        _next = next;
        _consistencyManager = consistencyManager;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip consistency handling for non-Cosmos paths
        if (ShouldSkip(path))
        {
            await _next(context);
            return;
        }

        // Parse the requested consistency level from header
        var requestedHeader = context.Request.Headers[CosmosHeaders.ConsistencyLevel].FirstOrDefault();
        ConsistencyLevel? requested = ParseConsistencyLevel(requestedHeader);

        // Validate: clients can only request same or weaker consistency than default
        if (requested.HasValue && !_consistencyManager.IsValidConsistencyLevel(requested.Value))
        {
            _logger.LogWarning(
                "Rejected consistency level override '{Requested}' (account default: '{Default}')",
                requestedHeader, _consistencyManager.DefaultConsistencyLevel);

            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/json";
            var payload = JsonSerializer.Serialize(new
            {
                code = "BadRequest",
                message = $"Requested consistency level '{requestedHeader}' is stronger than the account default '{_consistencyManager.DefaultConsistencyLevel}'. " +
                          "Clients can only request the same or weaker consistency level."
            }, s_jsonOptions);
            await context.Response.WriteAsync(payload);
            return;
        }

        // Compute effective consistency and store it for downstream use
        var effective = _consistencyManager.GetEffectiveConsistency(requested);
        context.Items[EffectiveConsistencyKey] = effective;

        // For read/query operations with session consistency, validate the session token
        if (effective == ConsistencyLevel.Session && IsReadOrQuery(context))
        {
            ValidateSessionTokenOnRead(context);
        }

        await _next(context);
    }

    private void ValidateSessionTokenOnRead(HttpContext context)
    {
        var sessionToken = context.Request.Headers[CosmosHeaders.SessionToken].FirstOrDefault();
        if (string.IsNullOrEmpty(sessionToken))
            return;

        // Extract database/container from path: /dbs/{dbId}/colls/{collId}/...
        var segments = (context.Request.Path.Value ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 ||
            !string.Equals(segments[0], "dbs", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[2], "colls", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var databaseId = segments[1];
        var containerId = segments[3];

        var isValid = _consistencyManager.ValidateSessionToken(databaseId, containerId, sessionToken);
        if (!isValid)
        {
            // Cosmos DB logs a warning but doesn't reject the request — it returns potentially stale data
            _logger.LogWarning(
                "Session token '{SessionToken}' is ahead of current LSN for {Database}/{Container}. " +
                "Returning available data (may be stale).",
                sessionToken, databaseId, containerId);
        }
    }

    private static bool IsReadOrQuery(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
            return true;

        // POST with isquery header is a query
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var isQuery = context.Request.Headers[CosmosHeaders.IsQuery].FirstOrDefault();
            return string.Equals(isQuery, "true", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool ShouldSkip(string path)
    {
        return path.StartsWith("/explorer", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/emulator", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);
    }

    private static ConsistencyLevel? ParseConsistencyLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "strong" => ConsistencyLevel.Strong,
            "boundedstaleness" => ConsistencyLevel.BoundedStaleness,
            "session" => ConsistencyLevel.Session,
            "consistentprefix" => ConsistencyLevel.ConsistentPrefix,
            "eventual" => ConsistencyLevel.Eventual,
            _ => null
        };
    }
}
