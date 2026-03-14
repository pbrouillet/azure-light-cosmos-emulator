namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Defines the partition key path(s) for a container.
/// </summary>
public class PartitionKeyDefinition
{
    /// <summary>Partition key paths (e.g., ["/tenantId"]).</summary>
    public required List<string> Paths { get; set; }

    /// <summary>Partition key kind (Hash or MultiHash).</summary>
    public PartitionKeyKind Kind { get; set; } = PartitionKeyKind.Hash;

    /// <summary>Version of the partition key definition (1 or 2).</summary>
    public int Version { get; set; } = 2;
}

/// <summary>
/// The kind of partition key.
/// </summary>
public enum PartitionKeyKind
{
    Hash,
    Range,
    MultiHash
}
