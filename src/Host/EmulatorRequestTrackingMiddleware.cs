using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Azure.Cosmos.LightEmulator.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class EmulatorRequestTrackingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RuTracker ruTracker)
    {
        if (!ShouldTrack(context.Request.Path))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? string.Empty;
        var (databaseId, containerId) = ExtractResourceIds(context.Request.Path);

        context.Response.OnStarting(() =>
        {
            var requestCharge = GetRequestCharge(context.Response.Headers[CosmosHeaders.RequestCharge].ToString());
            var activityId = context.Response.Headers[CosmosHeaders.ActivityId].ToString();
            if (string.IsNullOrWhiteSpace(activityId))
            {
                activityId = Guid.NewGuid().ToString();
                context.Response.Headers[CosmosHeaders.ActivityId] = activityId;
            }

            context.Response.Headers[CosmosHeaders.Diagnostics] = JsonSerializer.Serialize(new
            {
                latencyMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                requestCharge = Math.Round(requestCharge, 2),
                partitionId = "0",
                activityId
            });

            return Task.CompletedTask;
        });

        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var requestCharge = GetRequestCharge(context.Response.Headers[CosmosHeaders.RequestCharge].ToString());
            ruTracker.RecordRequest(
                requestCharge,
                method,
                path,
                context.Response.StatusCode,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                databaseId,
                containerId);
        }
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

    private static double GetRequestCharge(string headerValue)
    {
        if ((!string.IsNullOrWhiteSpace(headerValue)
                && double.TryParse(headerValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRequestCharge))
            || double.TryParse(headerValue, NumberStyles.Float, CultureInfo.CurrentCulture, out parsedRequestCharge))
        {
            return parsedRequestCharge;
        }

        return 1.0;
    }

    private static bool ShouldTrack(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return !value.Equals("/", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("/api/emulator/activity", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("/api/emulator/explain", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("/api/emulator/throughput", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("/explorer", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);
    }
}
