namespace Azure.Cosmos.LightEmulator.Core.Models;

/// <summary>
/// Cosmos DB response headers and request metadata.
/// </summary>
public static class CosmosHeaders
{
    // Request headers
    public const string Authorization = "Authorization";
    public const string ConsistencyLevel = "x-ms-consistency-level";
    public const string SessionToken = "x-ms-session-token";
    public const string PartitionKey = "x-ms-documentdb-partitionkey";
    public const string IsQuery = "x-ms-documentdb-isquery";
    public const string EnableCrossPartition = "x-ms-documentdb-query-enablecrosspartition";
    public const string Continuation = "x-ms-continuation";
    public const string MaxItemCount = "x-ms-max-item-count";
    public const string IfMatch = "If-Match";
    public const string IfNoneMatch = "If-None-Match";
    public const string IsUpsert = "x-ms-documentdb-is-upsert";
    public const string PreTriggerInclude = "x-ms-documentdb-pre-trigger-include";
    public const string PostTriggerInclude = "x-ms-documentdb-post-trigger-include";
    public const string IncrementalFeed = "A-IM";
    public const string ContentType = "Content-Type";

    // Response headers
    public const string RequestCharge = "x-ms-request-charge";
    public const string ActivityId = "x-ms-activity-id";
    public const string ItemCount = "x-ms-item-count";
    public const string ResourceQuota = "x-ms-resource-quota";
    public const string ResourceUsage = "x-ms-resource-usage";
    public const string RetryAfterMs = "x-ms-retry-after-ms";
    public const string SchemaVersion = "x-ms-schemaversion";
    public const string ServiceVersion = "x-ms-serviceversion";
    public const string GlobalCommittedLsn = "x-ms-global-committed-lsn";
    public const string NumberOfReadRegions = "x-ms-number-of-read-regions";
    public const string TransportRequestId = "x-ms-transport-request-id";
    public const string CosmosLlsn = "x-ms-cosmos-llsn";
    public const string CosmosItemLsn = "x-ms-cosmos-item-llsn";
    public const string LastStateChangeUtc = "x-ms-last-state-change-utc";
    public const string Diagnostics = "x-ms-cosmos-diagnostics";

    // Content types
    public const string JsonContentType = "application/json";
    public const string QueryJsonContentType = "application/query+json";

    // Incremental feed values
    public const string IncrementalFeedValue = "Incremental feed";

    // Service version
    public const string CurrentServiceVersion = "2024-11-30";
    public const string CurrentSchemaVersion = "1.18";
}
