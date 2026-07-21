//! Cosmos resource types: database, container, document, user, permission, offer.
//! Ports `CosmosDatabase.cs`, `CosmosContainer.cs`, `CosmosDocument.cs`,
//! `CosmosUser.cs`, `CosmosPermission.cs`, `CosmosOffer.cs`.

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::ids::{etag, resource_id};
use crate::models::partition_key::{PartitionKeyDefinition, PartitionKeyValue};
use crate::models::policies::{
    ConflictResolutionPolicy, IndexingPolicy, UniqueKeyPolicy, VectorEmbeddingPolicy,
};

fn now_ts() -> i64 {
    chrono::Utc::now().timestamp()
}

/// A JSON document body (Cosmos stores objects).
pub type JsonObject = Map<String, Value>;

// ---------- Database ----------

#[derive(Debug, Clone)]
pub struct CosmosDatabase {
    pub id: String,
    pub rid: String,
    pub etag: String,
    pub timestamp: i64,
    pub max_throughput: Option<i32>,
}

impl CosmosDatabase {
    pub fn new(id: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            rid: resource_id(),
            etag: etag(),
            timestamp: now_ts(),
            max_throughput: None,
        }
    }

    /// Self-link URI, e.g. `dbs/{rid}/`.
    pub fn self_link(&self) -> String {
        format!("dbs/{}/", self.rid)
    }
}

// ---------- Container ----------

#[derive(Debug, Clone)]
pub struct CosmosContainer {
    pub id: String,
    pub rid: String,
    pub self_link: String,
    pub etag: String,
    pub timestamp: i64,
    pub database_id: String,
    pub partition_key: PartitionKeyDefinition,
    pub indexing_policy: IndexingPolicy,
    pub default_time_to_live: Option<i32>,
    pub max_throughput: i32,
    pub unique_key_policy: Option<UniqueKeyPolicy>,
    pub conflict_resolution_policy: Option<ConflictResolutionPolicy>,
    pub vector_embedding_policy: Option<VectorEmbeddingPolicy>,
}

impl CosmosContainer {
    pub fn new(
        database_id: impl Into<String>,
        id: impl Into<String>,
        partition_key: PartitionKeyDefinition,
    ) -> Self {
        Self {
            id: id.into(),
            rid: resource_id(),
            self_link: String::new(),
            etag: etag(),
            timestamp: now_ts(),
            database_id: database_id.into(),
            partition_key,
            indexing_policy: IndexingPolicy::default(),
            default_time_to_live: None,
            max_throughput: 400,
            unique_key_policy: None,
            conflict_resolution_policy: None,
            vector_embedding_policy: None,
        }
    }
}

// ---------- Document ----------

#[derive(Debug, Clone)]
pub struct CosmosDocument {
    pub id: String,
    pub rid: String,
    pub self_link: String,
    pub etag: String,
    pub timestamp: i64,
    pub database_id: String,
    pub container_id: String,
    pub partition_key: PartitionKeyValue,
    pub body: JsonObject,
    pub time_to_live: Option<i32>,
    pub lsn: i64,
    pub is_indexed: bool,
}

impl CosmosDocument {
    pub fn new(
        database_id: impl Into<String>,
        container_id: impl Into<String>,
        id: impl Into<String>,
        partition_key: PartitionKeyValue,
        body: JsonObject,
    ) -> Self {
        Self {
            id: id.into(),
            rid: resource_id(),
            self_link: String::new(),
            etag: etag(),
            timestamp: now_ts(),
            database_id: database_id.into(),
            container_id: container_id.into(),
            partition_key,
            body,
            time_to_live: None,
            lsn: 0,
            is_indexed: true,
        }
    }

    /// Merges system properties into the body for serialization, mirroring
    /// `CosmosDocument.ToResponseBody()`.
    pub fn to_response_body(&self) -> JsonObject {
        let mut result = self.body.clone();
        result.insert("id".into(), Value::String(self.id.clone()));
        result.insert("_rid".into(), Value::String(self.rid.clone()));
        result.insert("_self".into(), Value::String(self.self_link.clone()));
        result.insert("_etag".into(), Value::String(self.etag.clone()));
        result.insert("_ts".into(), Value::from(self.timestamp));
        result.insert("_attachments".into(), Value::String("attachments/".into()));
        result
    }
}

// ---------- User ----------

#[derive(Debug, Clone)]
pub struct CosmosUser {
    pub id: String,
    pub rid: String,
    pub self_link: String,
    pub etag: String,
    pub timestamp: i64,
    pub database_id: String,
}

impl CosmosUser {
    pub fn new(database_id: impl Into<String>, id: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            rid: resource_id(),
            self_link: String::new(),
            etag: etag(),
            timestamp: now_ts(),
            database_id: database_id.into(),
        }
    }
}

// ---------- Permission ----------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum PermissionMode {
    Read,
    All,
}

#[derive(Debug, Clone)]
pub struct CosmosPermission {
    pub id: String,
    pub rid: String,
    pub self_link: String,
    pub etag: String,
    pub timestamp: i64,
    pub database_id: String,
    pub user_id: String,
    pub permission_mode: PermissionMode,
    /// Full addressable path of the resource, e.g. `dbs/db1/colls/coll1/`.
    pub resource: String,
    pub token: Option<String>,
}

// ---------- Offer ----------

#[derive(Debug, Clone)]
pub struct OfferContent {
    pub offer_throughput: i32,
}

#[derive(Debug, Clone)]
pub struct CosmosOffer {
    pub id: String,
    pub rid: String,
    pub etag: String,
    pub timestamp: i64,
    pub offer_version: String,
    pub offer_type: String,
    pub content: OfferContent,
    /// Self-link of the associated collection.
    pub resource: String,
    /// Resource ID (`_rid`) of the associated collection.
    pub offer_resource_id: String,
}

impl CosmosOffer {
    pub fn self_link(&self) -> String {
        format!("offers/{}/", self.id)
    }
}
