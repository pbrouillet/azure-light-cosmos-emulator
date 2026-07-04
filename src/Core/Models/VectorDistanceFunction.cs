namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// The distance/similarity function used for vector comparisons, matching the
/// Cosmos DB <c>distanceFunction</c> options for <c>VectorDistance</c> and the
/// container vector embedding policy.
/// </summary>
public enum VectorDistanceFunction
{
    /// <summary>Cosine similarity (higher is more similar).</summary>
    Cosine,

    /// <summary>Dot product similarity (higher is more similar).</summary>
    DotProduct,

    /// <summary>Euclidean (L2) distance (lower is closer).</summary>
    Euclidean
}

/// <summary>
/// Parsing helpers for <see cref="VectorDistanceFunction"/>.
/// </summary>
public static class VectorDistanceFunctions
{
    /// <summary>
    /// Parses a Cosmos DB distance-function string (case-insensitive). Accepts
    /// "cosine", "dotproduct"/"dot product", and "euclidean". Defaults to
    /// <see cref="VectorDistanceFunction.Cosine"/> for null/empty/unknown values.
    /// </summary>
    public static VectorDistanceFunction Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return VectorDistanceFunction.Cosine;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "cosine" => VectorDistanceFunction.Cosine,
            "dotproduct" or "dot product" => VectorDistanceFunction.DotProduct,
            "euclidean" => VectorDistanceFunction.Euclidean,
            _ => VectorDistanceFunction.Cosine
        };
    }
}
