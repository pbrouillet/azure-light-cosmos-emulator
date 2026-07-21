//! Cosmos DB header name and value constants. Ports `CosmosHeaders.cs`.

// Request headers
pub const AUTHORIZATION: &str = "Authorization";
pub const CONSISTENCY_LEVEL: &str = "x-ms-consistency-level";
pub const SESSION_TOKEN: &str = "x-ms-session-token";
pub const PARTITION_KEY: &str = "x-ms-documentdb-partitionkey";
pub const IS_QUERY: &str = "x-ms-documentdb-isquery";
pub const ENABLE_CROSS_PARTITION: &str = "x-ms-documentdb-query-enablecrosspartition";
pub const CONTINUATION: &str = "x-ms-continuation";
pub const MAX_ITEM_COUNT: &str = "x-ms-max-item-count";
pub const IF_MATCH: &str = "If-Match";
pub const IF_NONE_MATCH: &str = "If-None-Match";
pub const ENABLE_SCAN: &str = "x-ms-documentdb-query-enable-scan";
pub const IS_UPSERT: &str = "x-ms-documentdb-is-upsert";
pub const PRE_TRIGGER_INCLUDE: &str = "x-ms-documentdb-pre-trigger-include";
pub const POST_TRIGGER_INCLUDE: &str = "x-ms-documentdb-post-trigger-include";
pub const INDEXING_DIRECTIVE: &str = "x-ms-indexing-directive";
pub const IS_BATCH_REQUEST: &str = "x-ms-cosmos-is-batch-request";
pub const INCREMENTAL_FEED: &str = "A-IM";
pub const CONTENT_TYPE: &str = "Content-Type";

// Response headers
pub const REQUEST_CHARGE: &str = "x-ms-request-charge";
pub const ACTIVITY_ID: &str = "x-ms-activity-id";
pub const ITEM_COUNT: &str = "x-ms-item-count";
pub const RESOURCE_QUOTA: &str = "x-ms-resource-quota";
pub const RESOURCE_USAGE: &str = "x-ms-resource-usage";
pub const RETRY_AFTER_MS: &str = "x-ms-retry-after-ms";
pub const SCHEMA_VERSION: &str = "x-ms-schemaversion";
pub const SERVICE_VERSION: &str = "x-ms-serviceversion";
pub const GLOBAL_COMMITTED_LSN: &str = "x-ms-global-committed-lsn";
pub const NUMBER_OF_READ_REGIONS: &str = "x-ms-number-of-read-regions";
pub const TRANSPORT_REQUEST_ID: &str = "x-ms-transport-request-id";
pub const COSMOS_LLSN: &str = "x-ms-cosmos-llsn";
pub const COSMOS_ITEM_LSN: &str = "x-ms-cosmos-item-llsn";
pub const LAST_STATE_CHANGE_UTC: &str = "x-ms-last-state-change-utc";
pub const DIAGNOSTICS: &str = "x-ms-cosmos-diagnostics";
pub const PARTITION_KEY_RANGE_ID: &str = "x-ms-documentdb-partitionkeyrangeid";

// Content types
pub const JSON_CONTENT_TYPE: &str = "application/json";
pub const QUERY_JSON_CONTENT_TYPE: &str = "application/query+json";
pub const PATCH_JSON_CONTENT_TYPE: &str = "application/json_patch+json";

// Incremental feed value
pub const INCREMENTAL_FEED_VALUE: &str = "Incremental feed";

// Service / schema versions
pub const CURRENT_SERVICE_VERSION: &str = "2024-11-30";
pub const CURRENT_SCHEMA_VERSION: &str = "1.18";
