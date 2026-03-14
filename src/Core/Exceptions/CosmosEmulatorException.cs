using System.Net;

namespace Azure.Cosmos.LightEmulator.Core.Exceptions;

/// <summary>
/// Exception type that maps to Cosmos DB error responses.
/// </summary>
public class CosmosEmulatorException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
    public string ActivityId { get; } = Guid.NewGuid().ToString();
    public double RequestCharge { get; init; } = 1.0;

    public CosmosEmulatorException(HttpStatusCode statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public static CosmosEmulatorException NotFound(string resourceType, string resourceId) =>
        new(HttpStatusCode.NotFound, "NotFound",
            $"Resource {resourceType} with id '{resourceId}' was not found.");

    public static CosmosEmulatorException Conflict(string resourceType, string resourceId) =>
        new(HttpStatusCode.Conflict, "Conflict",
            $"Resource {resourceType} with id '{resourceId}' already exists.");

    public static CosmosEmulatorException PreconditionFailed(string message) =>
        new(HttpStatusCode.PreconditionFailed, "PreconditionFailed", message);

    public static CosmosEmulatorException BadRequest(string message) =>
        new(HttpStatusCode.BadRequest, "BadRequest", message);

    public static CosmosEmulatorException Unauthorized(string message) =>
        new(HttpStatusCode.Unauthorized, "Unauthorized", message);

    public static CosmosEmulatorException Forbidden(string message) =>
        new(HttpStatusCode.Forbidden, "Forbidden", message);

    public static CosmosEmulatorException MethodNotAllowed(string message) =>
        new(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed", message);

    public static CosmosEmulatorException TooManyRequests(string message, int retryAfterMs = 1000) =>
        new(HttpStatusCode.TooManyRequests, "TooManyRequests", message)
        {
            RequestCharge = 0
        };

    public static CosmosEmulatorException InternalServerError(string message) =>
        new(HttpStatusCode.InternalServerError, "InternalServerError", message);
}
