using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Azure.Cosmos.LightEmulator.Core.Exceptions;

namespace Azure.Cosmos.LightEmulator.NoSql.Middleware;

/// <summary>
/// Middleware that catches CosmosEmulatorException and returns proper error responses.
/// </summary>
public class CosmosExceptionMiddleware
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private readonly Microsoft.AspNetCore.Http.RequestDelegate _next;

    public CosmosExceptionMiddleware(Microsoft.AspNetCore.Http.RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(Microsoft.AspNetCore.Http.HttpContext context)
    {
        try
        {
            await _next(context);
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
