namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Vector similarity/distance computations shared by the vector index provider
/// and the query engine. All methods assume equal-length vectors.
/// </summary>
public static class VectorMath
{
    /// <summary>Cosine similarity in [-1, 1] (higher is more similar).</summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            magA += (double)a[i] * a[i];
            magB += (double)b[i] * b[i];
        }

        if (magA == 0 || magB == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    /// <summary>Dot product (higher is more similar).</summary>
    public static double DotProduct(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot;
    }

    /// <summary>Euclidean (L2) distance (lower is closer).</summary>
    public static double EuclideanDistance(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = (double)a[i] - b[i];
            sum += d * d;
        }

        return Math.Sqrt(sum);
    }

    /// <summary>
    /// The similarity/distance score as returned by the Cosmos DB
    /// <c>VectorDistance</c> function: similarity for cosine/dot product (higher
    /// is closer), raw distance for Euclidean (lower is closer).
    /// </summary>
    public static double Score(float[] a, float[] b, VectorDistanceFunction fn) => fn switch
    {
        VectorDistanceFunction.Cosine => CosineSimilarity(a, b),
        VectorDistanceFunction.DotProduct => DotProduct(a, b),
        VectorDistanceFunction.Euclidean => EuclideanDistance(a, b),
        _ => CosineSimilarity(a, b)
    };

    /// <summary>
    /// A monotonic distance where <b>lower always means closer</b>, suitable for
    /// nearest-first ordering and as the HNSW graph metric regardless of the
    /// underlying similarity function.
    /// </summary>
    public static float NearestFirstDistance(float[] a, float[] b, VectorDistanceFunction fn) => fn switch
    {
        VectorDistanceFunction.Cosine => (float)(1.0 - CosineSimilarity(a, b)),
        VectorDistanceFunction.DotProduct => (float)(-DotProduct(a, b)),
        VectorDistanceFunction.Euclidean => (float)EuclideanDistance(a, b),
        _ => (float)(1.0 - CosineSimilarity(a, b))
    };
}
