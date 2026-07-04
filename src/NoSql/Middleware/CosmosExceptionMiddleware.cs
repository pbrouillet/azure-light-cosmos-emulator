using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Cosmos.LightEmulator.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Azure.Cosmos.LightEmulator.NoSql.Middleware;

/// <summary>
/// Middleware that catches CosmosEmulatorException and returns proper error responses.
/// </summary>
public class CosmosExceptionMiddleware
{
    /// <summary>
    /// Non-standard "Client Closed Request" status code (Kestrel/nginx convention) used
    /// to record client disconnects for local telemetry. Never sent as a response body.
    /// </summary>
    private const int ClientClosedRequest = 499;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private readonly Microsoft.AspNetCore.Http.RequestDelegate _next;
    private readonly ILogger<CosmosExceptionMiddleware> _logger;

    public CosmosExceptionMiddleware(Microsoft.AspNetCore.Http.RequestDelegate next, ILogger<CosmosExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(Microsoft.AspNetCore.Http.HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected (RequestAborted fired). This is normal, not a
            // server fault — swallow it quietly so it is not logged as a 500 error.
            _logger.LogDebug("Request {Method} {Path} aborted by client.", context.Request.Method, context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
        }
        catch (CosmosEmulatorException ex)
        {
            context.Response.StatusCode = (int)ex.StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers["x-ms-request-charge"] = ex.RequestCharge.ToString("F2");
            context.Response.Headers["x-ms-activity-id"] = ex.ActivityId;

            var errorPayload = JsonSerializer.Serialize(new
            {
                code = ex.ErrorCode,
                message = ex.Message
            }, s_jsonOptions);
            var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorPayload);
            await context.Response.Body.WriteAsync(errorBytes);
        }
    }
}
