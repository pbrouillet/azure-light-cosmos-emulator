namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Represents a single patch operation for the Cosmos DB PATCH document API.
/// </summary>
public class PatchOperation
{
    /// <summary>Operation type: add, set, replace, remove, incr, move.</summary>
    public required string Op { get; set; }

    /// <summary>Target path (e.g., "/address/city").</summary>
    public required string Path { get; set; }

    /// <summary>Value for add/set/replace/incr operations.</summary>
    public object? Value { get; set; }

    /// <summary>Source path for move operations.</summary>
    public string? From { get; set; }
}

/// <summary>
/// Represents a PATCH document request body.
/// </summary>
public class PatchRequest
{
    /// <summary>Array of patch operations.</summary>
    public required List<PatchOperation> Operations { get; set; }

    /// <summary>Optional SQL condition for conditional patching.</summary>
    public string? Condition { get; set; }
}
