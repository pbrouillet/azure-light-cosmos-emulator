//! Core traits. Ports the interfaces under `src/Core/Interfaces/*`.
//!
//! These are the seams the storage, auth, query, and programmability layers
//! implement. Signatures mirror the .NET interfaces; the `CancellationToken`
//! parameter is dropped in favour of Rust's cooperative cancellation.

use async_trait::async_trait;

use crate::error::CosmosResult;
use crate::models::*;
use crate::ConsistencyLevel;

/// Storage abstraction. Ports `IDocumentStore` (databases, containers,
/// documents, users, permissions, offers, batch, bulk).
#[async_trait]
pub trait DocumentStore: Send + Sync {
    // Database operations
    async fn create_database(&self, id: &str) -> CosmosResult<CosmosDatabase>;
    async fn get_database(&self, id: &str) -> CosmosResult<CosmosDatabase>;
    async fn list_databases(&self) -> CosmosResult<FeedResponse<CosmosDatabase>>;
    async fn replace_database(&self, database: CosmosDatabase) -> CosmosResult<CosmosDatabase>;
    async fn delete_database(&self, id: &str) -> CosmosResult<()>;

    // Container operations
    async fn create_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer>;
    async fn get_container(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<CosmosContainer>;
    async fn list_containers(
        &self,
        database_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosContainer>>;
    async fn replace_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer>;
    async fn delete_container(&self, database_id: &str, container_id: &str) -> CosmosResult<()>;

    // Document operations
    async fn create_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument>;
    async fn read_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<CosmosDocument>;
    async fn replace_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        document: JsonObject,
        if_match: Option<&str>,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument>;
    async fn upsert_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument>;
    #[allow(clippy::too_many_arguments)]
    async fn patch_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
        operations: &[PatchOperation],
        if_match: Option<&str>,
        condition: Option<&str>,
    ) -> CosmosResult<CosmosDocument>;
    async fn delete_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<()>;
    async fn empty_container(&self, database_id: &str, container_id: &str) -> CosmosResult<usize>;
    async fn get_global_lsn(&self) -> CosmosResult<i64>;

    // Batch & bulk operations
    async fn execute_batch(
        &self,
        database_id: &str,
        container_id: &str,
        partition_key: &PartitionKeyValue,
        operations: &[BatchOperationRequest],
    ) -> CosmosResult<Vec<BatchOperationResponse>>;
    async fn read_many_documents(
        &self,
        database_id: &str,
        container_id: &str,
        items: &[(String, PartitionKeyValue)],
    ) -> CosmosResult<FeedResponse<CosmosDocument>>;
    async fn list_documents(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosDocument>>;

    // User operations
    async fn create_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser>;
    async fn get_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser>;
    async fn list_users(&self, database_id: &str) -> CosmosResult<FeedResponse<CosmosUser>>;
    async fn replace_user(&self, database_id: &str, user: CosmosUser) -> CosmosResult<CosmosUser>;
    async fn delete_user(&self, database_id: &str, user_id: &str) -> CosmosResult<()>;

    // Permission operations
    async fn create_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission>;
    async fn get_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<CosmosPermission>;
    async fn list_permissions(
        &self,
        database_id: &str,
        user_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosPermission>>;
    async fn replace_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission>;
    async fn delete_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<()>;

    // Offer operations
    async fn get_offer(&self, offer_id: &str) -> CosmosResult<CosmosOffer>;
    async fn list_offers(&self) -> CosmosResult<FeedResponse<CosmosOffer>>;
    async fn replace_offer(&self, offer: CosmosOffer) -> CosmosResult<CosmosOffer>;
}

/// Options for query execution. Ports `QueryOptions`.
#[derive(Debug, Clone, Default)]
pub struct QueryOptions {
    pub max_item_count: Option<i32>,
    pub continuation_token: Option<String>,
    pub enable_cross_partition_query: bool,
    pub partition_key: Option<PartitionKeyValue>,
    pub enable_scan: bool,
    pub consistency_level: Option<ConsistencyLevel>,
}

/// Cosmos SQL query engine. Ports `IQueryEngine`.
#[async_trait]
pub trait QueryEngine: Send + Sync {
    async fn execute_query(
        &self,
        database_id: &str,
        container_id: &str,
        query: &str,
        parameters: Option<&std::collections::HashMap<String, serde_json::Value>>,
        options: Option<QueryOptions>,
    ) -> CosmosResult<FeedResponse<JsonObject>>;
}

/// The authentication mechanism used for a request. Ports `AuthType`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthType {
    MasterKey,
    ResourceToken,
    EntraId,
}

/// Result of authentication validation. Ports `AuthResult`.
#[derive(Debug, Clone)]
pub struct AuthResult {
    pub is_authenticated: bool,
    pub auth_type: Option<AuthType>,
    pub error_message: Option<String>,
    pub principal: Option<String>,
}

impl AuthResult {
    pub fn success(auth_type: AuthType, principal: Option<String>) -> Self {
        Self {
            is_authenticated: true,
            auth_type: Some(auth_type),
            error_message: None,
            principal,
        }
    }

    pub fn failure(message: impl Into<String>) -> Self {
        Self {
            is_authenticated: false,
            auth_type: None,
            error_message: Some(message.into()),
            principal: None,
        }
    }
}

/// Authentication provider. Ports `IAuthProvider`.
#[async_trait]
pub trait AuthProvider: Send + Sync {
    async fn validate(
        &self,
        auth_header: &str,
        verb: &str,
        resource_type: &str,
        resource_link: &str,
        date_header: &str,
    ) -> AuthResult;
}

/// Options for reading the change feed. Ports `ChangeFeedOptions`.
#[derive(Debug, Clone, Default)]
pub struct ChangeFeedOptions {
    pub continuation_token: Option<String>,
    pub start_from_beginning: bool,
    pub start_time: Option<chrono::DateTime<chrono::Utc>>,
    pub max_item_count: Option<i32>,
    pub partition_key: Option<PartitionKeyValue>,
    pub full_fidelity: bool,
}

/// Change feed provider. Ports `IChangeFeedProvider`.
#[async_trait]
pub trait ChangeFeedProvider: Send + Sync {
    async fn read_change_feed(
        &self,
        database_id: &str,
        container_id: &str,
        options: ChangeFeedOptions,
    ) -> CosmosResult<FeedResponse<ChangeFeedItem>>;

    async fn record_change(
        &self,
        database_id: &str,
        container_id: &str,
        document: &CosmosDocument,
        change_type: ChangeType,
        previous_image: Option<&CosmosDocument>,
    ) -> CosmosResult<()>;

    async fn trim(&self, retention: std::time::Duration) -> CosmosResult<()>;
}

/// Consistency level & session token management. Ports `IConsistencyManager`.
pub trait ConsistencyManager: Send + Sync {
    fn default_consistency_level(&self) -> ConsistencyLevel;
    fn is_valid_consistency_level(&self, requested: ConsistencyLevel) -> bool;
    fn effective_consistency(&self, requested: Option<ConsistencyLevel>) -> ConsistencyLevel;
    fn generate_session_token(&self, database_id: &str, container_id: &str, lsn: i64) -> String;
    fn validate_session_token(
        &self,
        database_id: &str,
        container_id: &str,
        session_token: Option<&str>,
    ) -> bool;
    fn current_session_token(&self, database_id: &str, container_id: &str) -> String;
}

/// Engine for executing stored procedures, triggers, and UDFs.
/// Ports `IProgrammabilityEngine`.
#[async_trait]
pub trait ProgrammabilityEngine: Send + Sync {
    async fn create_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc: StoredProcedure,
    ) -> CosmosResult<StoredProcedure>;
    async fn list_stored_procedures(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<StoredProcedure>>;
    async fn execute_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc_id: &str,
        args: &[serde_json::Value],
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<Option<serde_json::Value>>;

    async fn create_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        trigger: Trigger,
    ) -> CosmosResult<Trigger>;
    async fn list_triggers(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<Trigger>>;

    async fn create_udf(
        &self,
        database_id: &str,
        container_id: &str,
        udf: UserDefinedFunction,
    ) -> CosmosResult<UserDefinedFunction>;
    async fn list_udfs(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<UserDefinedFunction>>;
}
