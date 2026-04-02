namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Defines the vector embedding policy for a container.
/// </summary>
public class VectorEmbeddingPolicy
{
    public List<VectorEmbedding> VectorEmbeddings { get; set; } = [];
}

/// <summary>
/// Describes a single vector embedding path within a container.
/// </summary>
public class VectorEmbedding
{
    public required string Path { get; set; }

    public string DataType { get; set; } = "float32";

    public string DistanceFunction { get; set; } = "cosine";

    public int Dimensions { get; set; } = 1536;
}
