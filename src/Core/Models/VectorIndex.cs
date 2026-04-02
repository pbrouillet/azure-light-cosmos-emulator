namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Defines a vector index on a specific path within a container's indexing policy.
/// </summary>
public class VectorIndex
{
    public required string Path { get; set; }

    /// <summary>
    /// The vector index type: "flat", "quantizedFlat", or "diskANN".
    /// </summary>
    public string Type { get; set; } = "flat";
}
