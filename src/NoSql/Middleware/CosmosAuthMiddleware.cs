using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.NoSql.Middleware;

/// <summary>
/// ASP.NET Core middleware for Cosmos DB authentication.
/// </summary>
public class CosmosAuthMiddleware
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private readonly Microsoft.AspNetCore.Http.RequestDelegate _next;
    private readonly IAuthProvider _authProvider;

    public CosmosAuthMiddleware(Microsoft.AspNetCore.Http.RequestDelegate next, IAuthProvider authProvider)
    {
        _next = next;
        _authProvider = authProvider;
    }

    public async Task InvokeAsync(Microsoft.AspNetCore.Http.HttpContext context)
    {
        // Skip auth for explorer and health endpoints
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/explorer", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/emulator/explain", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/emulator/throughput", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/pkranges", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Skip auth for requests originating from the local explorer UI
        var authHeader = context.Request.Headers[CosmosHeaders.Authorization].FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) && IsExplorerRequest(context))
        {
            context.Items["AuthResult"] = AuthResult.Success(AuthType.MasterKey, principal: "explorer");
            await _next(context);
            return;
        }

        if (string.IsNullOrEmpty(authHeader))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            var missingAuthPayload = JsonSerializer.Serialize(new
            {
                code = "Unauthorized",
                message = "Missing Authorization header."
            }, s_jsonOptions);
            var missingAuthBytes = System.Text.Encoding.UTF8.GetBytes(missingAuthPayload);
            await context.Response.Body.WriteAsync(missingAuthBytes);
            return;
        }

        var verb = context.Request.Method;
        var (resourceType, resourceLink) = ExtractResourceInfo(path);
        var dateHeader = context.Request.Headers["x-ms-date"].FirstOrDefault()
                         ?? context.Request.Headers["Date"].FirstOrDefault()
                         ?? "";

        var result = await _authProvider.ValidateAsync(authHeader, verb, resourceType, resourceLink, dateHeader);

        if (!result.IsAuthenticated)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            var invalidAuthPayload = JsonSerializer.Serialize(new
            {
                code = "Unauthorized",
                message = result.ErrorMessage
            }, s_jsonOptions);
            var invalidAuthBytes = System.Text.Encoding.UTF8.GetBytes(invalidAuthPayload);
            await context.Response.Body.WriteAsync(invalidAuthBytes);
            return;
        }

        // Store auth info for downstream use
        context.Items["AuthResult"] = result;
        await _next(context);
    }

    /// <summary>
    /// Extracts resource type and resource link from the request path.
    /// Maps URL segments to Cosmos DB resource types.
    /// </summary>
    private static (string resourceType, string resourceLink) ExtractResourceInfo(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return ("", "");

        // Pattern: dbs/{dbId}/colls/{collId}/docs/{docId}/...
        // Resource type is the last even-indexed segment (0-based: dbs, colls, docs, sprocs, triggers, udfs)
        // Resource link is the full path up to and including the last resource ID

        var resourceType = segments.Length switch
        {
            1 => segments[0],                    // "dbs"
            2 => segments[0],                    // "dbs" (for dbs/{id})
            3 => segments[2],                    // "colls"
            4 => segments[2],                    // "colls" (for colls/{id})
            5 => segments[4],                    // "docs", "sprocs", "triggers", "udfs"
            6 => segments[4],                    // "docs" (for docs/{id})
            _ => segments[^1]
        };

        var resourceLink = segments.Length switch
        {
            1 => "",                                                    // POST to /dbs
            2 => string.Join("/", segments[..2]),                       // GET /dbs/{id}
            3 => string.Join("/", segments[..2]),                       // POST to /dbs/{id}/colls
            4 => string.Join("/", segments[..4]),                       // GET /dbs/{id}/colls/{id}
            5 => string.Join("/", segments[..4]),                       // POST to /dbs/{id}/colls/{id}/docs
            6 => string.Join("/", segments[..6]),                       // GET /dbs/{id}/colls/{id}/docs/{id}
            _ => string.Join("/", segments)
        };

        return (resourceType.ToLowerInvariant(), resourceLink.ToLowerInvariant());
    }

    /// <summary>
    /// Detects requests originating from the local explorer UI.
    /// Matches same-origin requests with a Referer from /explorer, or
    /// requests using the 'same-origin' credentials mode (fetch with credentials: 'same-origin').
    /// </summary>
    private static bool IsExplorerRequest(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var referer = context.Request.Headers.Referer.FirstOrDefault();
        if (!string.IsNullOrEmpty(referer))
        {
            if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                // Same host and the path starts with /explorer
                var requestHost = context.Request.Host;
                if (string.Equals(refererUri.Host, requestHost.Host, StringComparison.OrdinalIgnoreCase)
                    && refererUri.AbsolutePath.StartsWith("/explorer", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Also accept requests with the x-ms-cosmos-explorer header (set by the explorer client)
        return !string.IsNullOrEmpty(context.Request.Headers["x-ms-cosmos-explorer"].FirstOrDefault());
    }
}
