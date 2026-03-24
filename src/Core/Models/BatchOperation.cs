using System.Text.Json.Nodes;

namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Operation types supported in a transactional batch.
/// </summary>
public enum BatchOperationType
{
    Create,
    Read,
    Replace,
    Upsert,
    Delete,
    Patch
}

/// <summary>
/// Represents a single operation within a transactional batch request.
/// </summary>
public class BatchOperationRequest
{
    public required BatchOperationType OperationType { get; set; }
    public string? Id { get; set; }
    public JsonObject? ResourceBody { get; set; }
    public string? IfMatch { get; set; }
    public string? IfNoneMatch { get; set; }
}

/// <summary>
/// Represents the result of a single operation within a transactional batch response.
/// </summary>
public class BatchOperationResponse
{
    public required int StatusCode { get; set; }
    public JsonObject? ResourceBody { get; set; }
    public string? ETag { get; set; }
    public double RequestCharge { get; set; }
    public int? RetryAfterMs { get; set; }
}
