namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Defines the indexing policy for a container.
/// </summary>
public class IndexingPolicy
{
    public bool Automatic { get; set; } = true;

    public IndexingMode IndexingMode { get; set; } = IndexingMode.Consistent;

    public List<IncludedPath> IncludedPaths { get; set; } = [new() { Path = "/*" }];

    public List<ExcludedPath> ExcludedPaths { get; set; } = [new() { Path = "/\"_etag\"/?" }];

    public List<CompositeIndex>? CompositeIndexes { get; set; }

    public List<SpatialIndex>? SpatialIndexes { get; set; }
}

public enum IndexingMode
{
    Consistent,
    Lazy,
    None
}

public class IncludedPath
{
    public required string Path { get; set; }
}

public class ExcludedPath
{
    public required string Path { get; set; }
}

public class CompositeIndex
{
    public required List<CompositeIndexPath> Paths { get; set; }
}

public class CompositeIndexPath
{
    public required string Path { get; set; }
    public SortOrder Order { get; set; } = SortOrder.Ascending;
}

public enum SortOrder
{
    Ascending,
    Descending
}

public class SpatialIndex
{
    public required string Path { get; set; }
    public required List<SpatialType> Types { get; set; }
}

public enum SpatialType
{
    Point,
    Polygon,
    MultiPolygon,
    LineString
}
