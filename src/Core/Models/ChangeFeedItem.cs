namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Change feed event for a document.
/// </summary>
public class ChangeFeedItem
{
    /// <summary>The document at the point of change.</summary>
    public required CosmosDocument Document { get; set; }

    /// <summary>Logical sequence number.</summary>
    public long Lsn { get; set; }

    /// <summary>Type of change.</summary>
    public ChangeType ChangeType { get; set; }

    /// <summary>The previous image of the document (for full fidelity mode).</summary>
    public CosmosDocument? PreviousImage { get; set; }

    /// <summary>Timestamp of the change.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public enum ChangeType
{
    Create,
    Replace,
    Delete
}
