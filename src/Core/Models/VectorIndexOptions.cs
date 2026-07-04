namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Runtime tuning options for the vector index. Bound from the emulator
/// configuration (see <c>EmulatorOptions.VectorIndex</c>) and consumed by the
/// storage-layer vector index provider.
/// </summary>
public sealed class VectorIndexOptions
{
    /// <summary>Master switch for index-accelerated vector search.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, embedding paths used in a <c>VectorDistance</c> ORDER BY are
    /// auto-indexed even if the container declares no vector policy. When false,
    /// only paths declared in the container's <c>VectorIndexes</c> are indexed.
    /// </summary>
    public bool ImplicitIndexing { get; set; } = true;

    /// <summary>HNSW graph connectivity (number of neighbours per node).</summary>
    public int M { get; set; } = 16;

    /// <summary>HNSW construction-time search width (higher = better recall, slower build).</summary>
    public int EfConstruction { get; set; } = 200;

    /// <summary>HNSW query-time search width (higher = better recall, slower query).</summary>
    public int EfSearch { get; set; } = 100;

    /// <summary>
    /// Fraction of tombstoned (deleted/updated) entries in a shard above which the
    /// HNSW graph is rebuilt from scratch to reclaim space and accuracy.
    /// </summary>
    public double RebuildTombstoneRatio { get; set; } = 0.25;

    /// <summary>
    /// For partition-scoped vector queries, when the target partition holds no more
    /// than this many live vectors an exact scan of just that partition is used
    /// (fast and exact) instead of a partition-filtered graph search.
    /// </summary>
    public int PartitionExactScanThreshold { get; set; } = 4096;

    /// <summary>
    /// When true, the HNSW graph for a shard is built on a background thread. The
    /// first queries fall back to an exact brute-force scan while the graph is
    /// under construction and transparently switch to index acceleration once it
    /// is ready, avoiding a multi-second stall on the first query of a large
    /// container.
    /// </summary>
    public bool BackgroundBuild { get; set; } = true;
}
