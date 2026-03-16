using System.Globalization;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.Host;

/// <summary>
/// Middleware that enforces provisioned RU/s caps at the database and container level.
/// Returns 429 (Too Many Requests) with x-ms-retry-after-ms when the budget is exceeded.
/// </summary>
public sealed class ThroughputEnforcementMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ThroughputManager throughputManager,
        IDocumentStore documentStore,
        ILogger<ThroughputEnforcementMiddleware> logger)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/explorer", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var (databaseId, containerId) = ExtractResourceIds(context.Request.Path);
        if (string.IsNullOrEmpty(databaseId))
        {
            await next(context);
            return;
        }

        // Estimate request charge upfront (use a rough minimum; the actual charge is computed by the controller)
        var estimatedCharge = EstimateCharge(context.Request.Method);

        // Check database-level cap
        try
        {
            var database = await documentStore.GetDatabaseAsync(databaseId, context.RequestAborted);
            if (database.MaxThroughput is > 0)
            {
                if (!throughputManager.TryConsumeDatabase(databaseId, database.MaxThroughput.Value, estimatedCharge, out var dbRetryMs))
                {
                    logger.LogWarning("Database {DatabaseId} RU budget exceeded. Retry after {RetryMs}ms", databaseId, dbRetryMs);
                    await Write429Async(context, dbRetryMs);
                    return;
                }
            }
        }
        catch
        {
            // Database may not exist yet (e.g. creating it); let the request through
        }

        // Check container-level cap
        if (!string.IsNullOrEmpty(containerId))
        {
            try
            {
                var container = await documentStore.GetContainerAsync(databaseId, containerId, context.RequestAborted);
                if (container.MaxThroughput > 0)
                {
                    if (!throughputManager.TryConsume(databaseId, containerId, container.MaxThroughput, estimatedCharge, out var containerRetryMs))
                    {
                        logger.LogWarning("Container {DatabaseId}/{ContainerId} RU budget exceeded. Retry after {RetryMs}ms", databaseId, containerId, containerRetryMs);
                        await Write429Async(context, containerRetryMs);
                        return;
                    }
                }
            }
            catch
            {
                // Container may not exist yet; let the request through
            }
        }

        await next(context);
    }

    private static double EstimateCharge(string method) => method.ToUpperInvariant() switch
    {
        "GET" => 1.0,
        "POST" => 5.0,
        "PUT" => 5.0,
        "DELETE" => 5.0,
        _ => 1.0,
    };

    private static async Task Write429Async(HttpContext context, int retryAfterMs)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers[CosmosHeaders.RetryAfterMs] = retryAfterMs.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = CosmosHeaders.JsonContentType;
        await context.Response.WriteAsync(
            $"{{\"code\":\"429\",\"message\":\"Request rate is large. Retry after {retryAfterMs} milliseconds.\"}}");
    }

    private static (string? DatabaseId, string? ContainerId) ExtractResourceIds(PathString path)
    {
        var segments = (path.Value ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? databaseId = null;
        string? containerId = null;

        for (var i = 0; i < segments.Length; i++)
        {
            if (databaseId is null
                && segments[i].Equals("dbs", StringComparison.OrdinalIgnoreCase)
                && i + 1 < segments.Length)
            {
                databaseId = Uri.UnescapeDataString(segments[i + 1]);
            }

            if (containerId is null
                && segments[i].Equals("colls", StringComparison.OrdinalIgnoreCase)
                && i + 1 < segments.Length)
            {
                containerId = Uri.UnescapeDataString(segments[i + 1]);
            }
        }

        return (databaseId, containerId);
    }
}
