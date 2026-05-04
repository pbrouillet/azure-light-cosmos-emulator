using Azure.Cosmos.LightEmulator.Core.Models;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

/// <summary>
/// Provides change feed functionality for tracking document changes.
/// </summary>
public interface IChangeFeedProvider
{
    /// <summary>
    /// Reads changes from the change feed for a container.
    /// </summary>
    /// <param name="databaseId">Database identifier.</param>
    /// <param name="containerId">Container identifier.</param>
    /// <param name="options">Change feed read options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Feed response with changed documents.</returns>
    Task<FeedResponse<ChangeFeedItem>> ReadChangeFeedAsync(
        string databaseId,
        string containerId,
        ChangeFeedOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Records a document change in the change feed.
    /// </summary>
    Task RecordChangeAsync(
        string databaseId,
        string containerId,
        CosmosDocument document,
        ChangeType changeType,
        CosmosDocument? previousImage = null,
        CancellationToken ct = default);

    /// <summary>
    /// Trims change feed entries older than <paramref name="retention"/>.
    /// </summary>
    Task TrimAsync(TimeSpan retention, CancellationToken ct = default);
}

/// <summary>
/// Options for reading the change feed.
/// </summary>
public class ChangeFeedOptions
{
    /// <summary>Continuation token (LSN-based).</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Start from the beginning of the feed.</summary>
    public bool StartFromBeginning { get; set; }

    /// <summary>Start from a specific point in time.</summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>Maximum number of items to return.</summary>
    public int? MaxItemCount { get; set; }

    /// <summary>Partition key range to read from.</summary>
    public PartitionKeyValue? PartitionKey { get; set; }

    /// <summary>Enable full fidelity mode (all versions and deletes).</summary>
    public bool FullFidelity { get; set; }
}
