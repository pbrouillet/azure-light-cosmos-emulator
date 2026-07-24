//! SurrealDB-backed `DocumentStore` (and change-feed provider).
//!
//! Ports `SurrealDbDocumentStore`, `SurrealDbConnectionManager`, and
//! `SurrealDbChangeFeedProvider`. The .NET version embeds SurrealDB with a
//! RocksDB KV backend; this port uses the embedded **SurrealKV** engine (pure
//! Rust, persistent) which builds cleanly without a C++ toolchain. Cross-process
//! data-file compatibility with the .NET store is not a goal, so the storage
//! engine choice is transparent to callers.
//!
//! Records are stored one table per resource type (`cosmos_databases`,
//! `cosmos_containers`, `cosmos_documents`, …). Complex sub-objects (partition
//! key definitions, policies, document bodies) are persisted as JSON strings,
//! mirroring the column layout of the SQLite backend so both backends behave
//! identically at the trait boundary.

use std::path::Path;
use std::sync::atomic::{AtomicI64, Ordering};

use async_trait::async_trait;
use base64::Engine;
use chrono::{TimeZone, Utc};
use cosmos_core::error::{CosmosError, CosmosResult};
use cosmos_core::ids::etag;
use cosmos_core::models::*;
use cosmos_core::traits::{
    ActivityStore, ChangeFeedOptions, ChangeFeedProvider, DocumentStore, QueryTelemetryStore,
};
use serde::{Deserialize, Serialize};
use surrealdb::engine::local::{Db, SurrealKv};
use surrealdb::Surreal;

use crate::changefeed::page_items;
use crate::common::{
    apply_patch, deserialize_partition_key, extract_partition_key, require_id,
    serialize_partition_key, MAX_DOCUMENT_SIZE_BYTES,
};
use crate::programmability::{
    ProgrammabilityRecord, ProgrammabilityRecordStore, ProgrammabilityTable,
};

const DATABASE_TABLE: &str = "cosmos_databases";
const CONTAINER_TABLE: &str = "cosmos_containers";
const DOCUMENT_TABLE: &str = "cosmos_documents";
const USER_TABLE: &str = "cosmos_users";
const PERMISSION_TABLE: &str = "cosmos_permissions";
const OFFER_TABLE: &str = "cosmos_offers";
const META_TABLE: &str = "cosmos_meta";
const CHANGEFEED_TABLE: &str = "cosmos_changefeed";
const ACTIVITY_TABLE: &str = "cosmos_activity_log";
const QUERY_TELEMETRY_TABLE: &str = "cosmos_query_telemetry";
const SPROC_TABLE: &str = "cosmos_sprocs";
const TRIGGER_TABLE: &str = "cosmos_triggers";
const UDF_TABLE: &str = "cosmos_udfs";
const GLOBAL_LSN_KEY: &str = "global_lsn";
const NAMESPACE: &str = "emulator";
const DATABASE: &str = "cosmos";

// ─── Persisted record shapes ─────────────────────────────────────────────────
// NB: none of these use a field literally named `id`, because SurrealDB reserves
// the `id` field for the record's own key; a domain id is stored as `cosmos_id`.

#[derive(Serialize, Deserialize)]
struct DatabaseRow {
    cosmos_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    max_throughput: Option<i32>,
}

#[derive(Serialize, Deserialize)]
struct ContainerRow {
    cosmos_id: String,
    database_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    partition_key_json: String,
    indexing_policy_json: String,
    default_ttl: Option<i32>,
    max_throughput: i32,
    unique_key_policy_json: Option<String>,
    conflict_resolution_policy_json: Option<String>,
    vector_embedding_policy_json: Option<String>,
}

#[derive(Serialize, Deserialize)]
struct DocumentRow {
    cosmos_id: String,
    database_id: String,
    container_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    partition_key_json: String,
    body_json: String,
    ttl: Option<i32>,
    lsn: i64,
    is_indexed: bool,
}

#[derive(Serialize, Deserialize)]
struct UserRow {
    cosmos_id: String,
    database_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
}

#[derive(Serialize, Deserialize)]
struct PermissionRow {
    cosmos_id: String,
    database_id: String,
    user_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    permission_mode: String,
    resource: String,
    token: Option<String>,
}

#[derive(Serialize, Deserialize)]
struct OfferRow {
    cosmos_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    offer_version: String,
    offer_type: String,
    offer_throughput: i32,
    resource: String,
    offer_resource_id: String,
}

#[derive(Serialize, Deserialize)]
struct MetaRow {
    value: i64,
}

#[derive(Serialize, Deserialize, Clone)]
struct ChangeRow {
    database_id: String,
    container_id: String,
    document_id: String,
    lsn: i64,
    change_type: i64,
    body_json: String,
    previous_image_json: Option<String>,
    partition_key_json: String,
    timestamp: i64,
}

#[derive(Serialize, Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
struct ActivityRow {
    method: String,
    path: String,
    status_code: i32,
    request_charge: f64,
    latency_ms: f64,
    database_id: Option<String>,
    container_id: Option<String>,
    timestamp: i64,
}

#[derive(Serialize, Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
struct QueryTelemetryRow {
    database_id: String,
    container_id: String,
    sql_text: String,
    partition_key: Option<String>,
    consistency_level: String,
    request_charge: f64,
    latency_ms: i64,
    item_count: i32,
    status_code: i32,
    activity_id: String,
    continuation_token: Option<String>,
    is_cross_partition: bool,
    timestamp: i64,
    query_plan: Option<String>,
}

#[derive(Serialize, Deserialize, Clone)]
struct SprocRow {
    cosmos_id: String,
    database_id: String,
    container_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    body: String,
}

#[derive(Serialize, Deserialize, Clone)]
struct TriggerRow {
    cosmos_id: String,
    database_id: String,
    container_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    body: String,
    trigger_type: i32,
    trigger_operation: i32,
}

#[derive(Serialize, Deserialize, Clone)]
struct UdfRow {
    cosmos_id: String,
    database_id: String,
    container_id: String,
    rid: String,
    etag: String,
    timestamp: i64,
    body: String,
}

// ─── Store ───────────────────────────────────────────────────────────────────

/// SurrealDB (embedded SurrealKV) implementation of [`DocumentStore`].
pub struct SurrealDbDocumentStore {
    db: Surreal<Db>,
    next_lsn: AtomicI64,
}

/// SurrealDB-backed programmability record store.
pub struct SurrealDbProgrammabilityRecordStore {
    db: Surreal<Db>,
}

impl SurrealDbDocumentStore {
    /// Opens (or creates) the embedded SurrealKV database under `data_dir`.
    pub async fn open(data_dir: impl AsRef<Path>) -> CosmosResult<Self> {
        let dir = data_dir.as_ref();
        std::fs::create_dir_all(dir)
            .map_err(|e| CosmosError::internal_server_error(e.to_string()))?;
        let path = dir.join("surreal.db");
        let db = Surreal::new::<SurrealKv>(path.to_string_lossy().to_string())
            .await
            .map_err(surreal_err)?;
        db.use_ns(NAMESPACE)
            .use_db(DATABASE)
            .await
            .map_err(surreal_err)?;
        Self::from_db(db).await
    }

    /// Creates a store backed by an in-memory SurrealKV instance (used by tests).
    pub async fn in_memory() -> CosmosResult<Self> {
        // A unique workspace-local path per instance keeps parallel tests isolated.
        let path = std::env::current_dir()
            .map_err(|e| CosmosError::internal_server_error(e.to_string()))?
            .join("target")
            .join("cosmos-surreal")
            .join(uuid_like());
        Self::open(path).await
    }

    async fn from_db(db: Surreal<Db>) -> CosmosResult<Self> {
        let seed = Self::seed_lsn(&db).await?;
        Ok(Self {
            db,
            next_lsn: AtomicI64::new(seed),
        })
    }

    /// Determines the starting LSN as the max of the persisted meta value and the
    /// highest document LSN, so replays after restart never reuse an LSN.
    async fn seed_lsn(db: &Surreal<Db>) -> CosmosResult<i64> {
        let meta: Option<MetaRow> = db
            .select((META_TABLE, encode_key(GLOBAL_LSN_KEY)))
            .await
            .map_err(surreal_err)?;
        let docs: Vec<DocumentRow> = db.select(DOCUMENT_TABLE).await.map_err(surreal_err)?;
        let max_doc = docs.iter().map(|d| d.lsn).max().unwrap_or(0);
        Ok(meta.map(|m| m.value).unwrap_or(0).max(max_doc))
    }

    /// Returns a change-feed provider sharing this store's connection, so it reads
    /// the same `cosmos_changefeed` table this store writes to.
    pub fn change_feed(&self) -> SurrealDbChangeFeedProvider {
        SurrealDbChangeFeedProvider {
            db: self.db.clone(),
        }
    }

    /// Returns an activity store sharing this store's SurrealDB connection.
    pub fn activity_store(&self) -> SurrealDbActivityStore {
        SurrealDbActivityStore {
            db: self.db.clone(),
        }
    }

    /// Returns a query telemetry store sharing this store's SurrealDB connection.
    pub fn query_telemetry_store(&self) -> SurrealDbQueryTelemetryStore {
        SurrealDbQueryTelemetryStore {
            db: self.db.clone(),
        }
    }

    /// Returns a programmability record store sharing this SurrealDB connection.
    pub fn programmability_store(&self) -> SurrealDbProgrammabilityRecordStore {
        SurrealDbProgrammabilityRecordStore {
            db: self.db.clone(),
        }
    }

    async fn next_lsn(&self) -> CosmosResult<i64> {
        let next = self.next_lsn.fetch_add(1, Ordering::SeqCst) + 1;
        let _: Option<MetaRow> = self
            .db
            .upsert((META_TABLE, encode_key(GLOBAL_LSN_KEY)))
            .content(MetaRow { value: next })
            .await
            .map_err(surreal_err)?;
        Ok(next)
    }

    async fn record_change(
        &self,
        doc: &CosmosDocument,
        change_type: ChangeType,
        previous_image: Option<&CosmosDocument>,
    ) -> CosmosResult<()> {
        let row = ChangeRow {
            database_id: doc.database_id.clone(),
            container_id: doc.container_id.clone(),
            document_id: doc.id.clone(),
            lsn: doc.lsn,
            change_type: change_type_code(change_type),
            body_json: to_json(&doc.body),
            previous_image_json: previous_image.map(|p| to_json(&p.body)),
            partition_key_json: serialize_partition_key(&doc.partition_key),
            timestamp: Utc::now().timestamp_millis(),
        };
        let key = format!(
            "{}:{}:{}",
            encode_key(&doc.database_id),
            encode_key(&doc.container_id),
            encode_key(&doc.lsn.to_string())
        );
        let _: Option<ChangeRow> = self
            .db
            .upsert((CHANGEFEED_TABLE, key))
            .content(row)
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    async fn select_container_row(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<CosmosContainer> {
        let row: Option<ContainerRow> = self
            .db
            .select((CONTAINER_TABLE, container_key(database_id, container_id)))
            .await
            .map_err(surreal_err)?;
        row.map(row_to_container)
            .transpose()?
            .ok_or_else(|| CosmosError::not_found("container", container_id))
    }

    async fn select_document_row(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        pk: &PartitionKeyValue,
    ) -> CosmosResult<Option<CosmosDocument>> {
        let row: Option<DocumentRow> = self
            .db
            .select((
                DOCUMENT_TABLE,
                document_key(database_id, container_id, document_id, pk),
            ))
            .await
            .map_err(surreal_err)?;
        row.map(row_to_document).transpose()
    }

    async fn upsert_document_row(&self, doc: &CosmosDocument) -> CosmosResult<()> {
        let key = document_key(
            &doc.database_id,
            &doc.container_id,
            &doc.id,
            &doc.partition_key,
        );
        let _: Option<DocumentRow> = self
            .db
            .upsert((DOCUMENT_TABLE, key))
            .content(document_to_row(doc))
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    async fn ensure_database_exists(&self, database_id: &str) -> CosmosResult<()> {
        let row: Option<DatabaseRow> = self
            .db
            .select((DATABASE_TABLE, encode_key(database_id)))
            .await
            .map_err(surreal_err)?;
        row.map(|_| ())
            .ok_or_else(|| CosmosError::not_found("database", database_id))
    }
}

/// SurrealDB-backed activity log store.
pub struct SurrealDbActivityStore {
    db: Surreal<Db>,
}

#[async_trait]
impl ActivityStore for SurrealDbActivityStore {
    async fn record(&self, entry: ActivityEntry) -> CosmosResult<()> {
        let row = ActivityRow {
            method: entry.method,
            path: entry.path,
            status_code: entry.status_code,
            request_charge: entry.request_charge,
            latency_ms: entry.latency_ms,
            database_id: entry.database_id,
            container_id: entry.container_id,
            timestamp: entry.timestamp.timestamp_millis(),
        };
        let _: Option<ActivityRow> = self
            .db
            .upsert((ACTIVITY_TABLE, encode_key(&uuid_like())))
            .content(row)
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    async fn list(&self, max_items: i32) -> CosmosResult<Vec<ActivityEntry>> {
        let mut rows: Vec<ActivityRow> =
            self.db.select(ACTIVITY_TABLE).await.map_err(surreal_err)?;
        rows.sort_by_key(|e| std::cmp::Reverse(e.timestamp));
        rows.truncate(max_items.max(0) as usize);
        Ok(rows
            .into_iter()
            .map(|row| ActivityEntry {
                timestamp: Utc
                    .timestamp_millis_opt(row.timestamp)
                    .single()
                    .unwrap_or_else(Utc::now),
                method: row.method,
                path: row.path,
                status_code: row.status_code,
                request_charge: row.request_charge,
                latency_ms: row.latency_ms,
                database_id: row.database_id,
                container_id: row.container_id,
            })
            .collect())
    }

    async fn clear(&self) -> CosmosResult<()> {
        let _: Vec<ActivityRow> = self.db.delete(ACTIVITY_TABLE).await.map_err(surreal_err)?;
        Ok(())
    }

    async fn trim(&self, max_entries: i32) -> CosmosResult<()> {
        let rows: Vec<ActivityRow> = self.db.select(ACTIVITY_TABLE).await.map_err(surreal_err)?;
        if rows.len() > max_entries.max(0) as usize {
            self.clear().await?;
        }
        Ok(())
    }
}

/// SurrealDB-backed query telemetry store.
pub struct SurrealDbQueryTelemetryStore {
    db: Surreal<Db>,
}

#[async_trait]
impl QueryTelemetryStore for SurrealDbQueryTelemetryStore {
    async fn record(&self, entry: QueryTelemetryEntry) -> CosmosResult<()> {
        let row = QueryTelemetryRow {
            database_id: entry.database_id,
            container_id: entry.container_id,
            sql_text: entry.sql_text,
            partition_key: entry.partition_key,
            consistency_level: entry.consistency_level,
            request_charge: entry.request_charge,
            latency_ms: entry.latency_ms,
            item_count: entry.item_count,
            status_code: entry.status_code,
            activity_id: entry.activity_id,
            continuation_token: entry.continuation_token,
            is_cross_partition: entry.is_cross_partition,
            timestamp: entry.timestamp.timestamp_millis(),
            query_plan: entry.query_plan,
        };
        let _: Option<QueryTelemetryRow> = self
            .db
            .upsert((QUERY_TELEMETRY_TABLE, encode_key(&entry.id)))
            .content(row)
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    async fn list(
        &self,
        database_id: Option<&str>,
        container_id: Option<&str>,
        max_items: i32,
    ) -> CosmosResult<Vec<QueryTelemetryEntry>> {
        let mut rows: Vec<QueryTelemetryRow> = self
            .db
            .select(QUERY_TELEMETRY_TABLE)
            .await
            .map_err(surreal_err)?;
        rows.sort_by_key(|e| std::cmp::Reverse(e.timestamp));
        let entries = rows
            .into_iter()
            .filter(|row| {
                database_id.is_none_or(|db| row.database_id.eq_ignore_ascii_case(db))
                    && container_id
                        .is_none_or(|container| row.container_id.eq_ignore_ascii_case(container))
            })
            .take(max_items.max(0) as usize)
            .map(|row| {
                let default = QueryTelemetryEntry::default();
                QueryTelemetryEntry {
                    id: default.id,
                    timestamp: Utc
                        .timestamp_millis_opt(row.timestamp)
                        .single()
                        .unwrap_or_else(Utc::now),
                    database_id: row.database_id,
                    container_id: row.container_id,
                    sql_text: row.sql_text,
                    partition_key: row.partition_key,
                    consistency_level: row.consistency_level,
                    request_charge: row.request_charge,
                    latency_ms: row.latency_ms,
                    item_count: row.item_count,
                    status_code: row.status_code,
                    activity_id: row.activity_id,
                    continuation_token: row.continuation_token,
                    is_cross_partition: row.is_cross_partition,
                    query_plan: row.query_plan,
                }
            })
            .collect();
        Ok(entries)
    }

    async fn clear(&self) -> CosmosResult<()> {
        let _: Vec<QueryTelemetryRow> = self
            .db
            .delete(QUERY_TELEMETRY_TABLE)
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    async fn trim(&self, max_entries: i32) -> CosmosResult<()> {
        let rows: Vec<QueryTelemetryRow> = self
            .db
            .select(QUERY_TELEMETRY_TABLE)
            .await
            .map_err(surreal_err)?;
        if rows.len() > max_entries.max(0) as usize {
            self.clear().await?;
        }
        Ok(())
    }
}

#[async_trait]
impl ProgrammabilityRecordStore for SurrealDbProgrammabilityRecordStore {
    async fn select_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
    ) -> CosmosResult<Option<ProgrammabilityRecord>> {
        match table {
            ProgrammabilityTable::StoredProcedures => {
                let row: Option<SprocRow> = self
                    .db
                    .select((SPROC_TABLE, record_key.to_string()))
                    .await
                    .map_err(surreal_err)?;
                Ok(row.map(|r| ProgrammabilityRecord::StoredProcedure(row_to_sproc(r))))
            }
            ProgrammabilityTable::Triggers => {
                let row: Option<TriggerRow> = self
                    .db
                    .select((TRIGGER_TABLE, record_key.to_string()))
                    .await
                    .map_err(surreal_err)?;
                Ok(row.map(|r| ProgrammabilityRecord::Trigger(row_to_trigger(r))))
            }
            ProgrammabilityTable::UserDefinedFunctions => {
                let row: Option<UdfRow> = self
                    .db
                    .select((UDF_TABLE, record_key.to_string()))
                    .await
                    .map_err(surreal_err)?;
                Ok(row.map(|r| ProgrammabilityRecord::UserDefinedFunction(row_to_udf(r))))
            }
        }
    }

    async fn select_table_records(
        &self,
        table: ProgrammabilityTable,
    ) -> CosmosResult<Vec<ProgrammabilityRecord>> {
        match table {
            ProgrammabilityTable::StoredProcedures => {
                let mut rows: Vec<SprocRow> =
                    self.db.select(SPROC_TABLE).await.map_err(surreal_err)?;
                rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
                Ok(rows
                    .into_iter()
                    .map(|r| ProgrammabilityRecord::StoredProcedure(row_to_sproc(r)))
                    .collect())
            }
            ProgrammabilityTable::Triggers => {
                let mut rows: Vec<TriggerRow> =
                    self.db.select(TRIGGER_TABLE).await.map_err(surreal_err)?;
                rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
                Ok(rows
                    .into_iter()
                    .map(|r| ProgrammabilityRecord::Trigger(row_to_trigger(r)))
                    .collect())
            }
            ProgrammabilityTable::UserDefinedFunctions => {
                let mut rows: Vec<UdfRow> = self.db.select(UDF_TABLE).await.map_err(surreal_err)?;
                rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
                Ok(rows
                    .into_iter()
                    .map(|r| ProgrammabilityRecord::UserDefinedFunction(row_to_udf(r)))
                    .collect())
            }
        }
    }

    async fn create_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
        record: ProgrammabilityRecord,
    ) -> CosmosResult<()> {
        self.upsert_record(table, record_key, record).await
    }

    async fn upsert_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
        record: ProgrammabilityRecord,
    ) -> CosmosResult<()> {
        match (table, record) {
            (ProgrammabilityTable::StoredProcedures, ProgrammabilityRecord::StoredProcedure(s)) => {
                let _: Option<SprocRow> = self
                    .db
                    .upsert((SPROC_TABLE, record_key.to_string()))
                    .content(sproc_to_row(&s))
                    .await
                    .map_err(surreal_err)?;
            }
            (ProgrammabilityTable::Triggers, ProgrammabilityRecord::Trigger(t)) => {
                let _: Option<TriggerRow> = self
                    .db
                    .upsert((TRIGGER_TABLE, record_key.to_string()))
                    .content(trigger_to_row(&t))
                    .await
                    .map_err(surreal_err)?;
            }
            (
                ProgrammabilityTable::UserDefinedFunctions,
                ProgrammabilityRecord::UserDefinedFunction(u),
            ) => {
                let _: Option<UdfRow> = self
                    .db
                    .upsert((UDF_TABLE, record_key.to_string()))
                    .content(udf_to_row(&u))
                    .await
                    .map_err(surreal_err)?;
            }
            _ => {
                return Err(CosmosError::internal_server_error(
                    "programmability table/record mismatch",
                ))
            }
        }
        Ok(())
    }

    async fn delete_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
        resource_type: &str,
        resource_id: &str,
    ) -> CosmosResult<()> {
        let deleted = match table {
            ProgrammabilityTable::StoredProcedures => {
                let row: Option<SprocRow> = self
                    .db
                    .delete((SPROC_TABLE, record_key.to_string()))
                    .await
                    .map_err(surreal_err)?;
                row.is_some()
            }
            ProgrammabilityTable::Triggers => {
                let row: Option<TriggerRow> = self
                    .db
                    .delete((TRIGGER_TABLE, record_key.to_string()))
                    .await
                    .map_err(surreal_err)?;
                row.is_some()
            }
            ProgrammabilityTable::UserDefinedFunctions => {
                let row: Option<UdfRow> = self
                    .db
                    .delete((UDF_TABLE, record_key.to_string()))
                    .await
                    .map_err(surreal_err)?;
                row.is_some()
            }
        };
        if !deleted {
            return Err(CosmosError::not_found(resource_type, resource_id));
        }
        Ok(())
    }
}

#[async_trait]
impl DocumentStore for SurrealDbDocumentStore {
    // ---------- Databases ----------

    async fn create_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        let existing: Option<DatabaseRow> = self
            .db
            .select((DATABASE_TABLE, encode_key(id)))
            .await
            .map_err(surreal_err)?;
        if existing.is_some() {
            return Err(CosmosError::conflict("database", id));
        }
        let db = CosmosDatabase::new(id);
        let _: Option<DatabaseRow> = self
            .db
            .create((DATABASE_TABLE, encode_key(id)))
            .content(database_to_row(&db))
            .await
            .map_err(surreal_err)?;
        Ok(db)
    }

    async fn get_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        let row: Option<DatabaseRow> = self
            .db
            .select((DATABASE_TABLE, encode_key(id)))
            .await
            .map_err(surreal_err)?;
        row.map(row_to_database)
            .ok_or_else(|| CosmosError::not_found("database", id))
    }

    async fn list_databases(&self) -> CosmosResult<FeedResponse<CosmosDatabase>> {
        let mut rows: Vec<DatabaseRow> =
            self.db.select(DATABASE_TABLE).await.map_err(surreal_err)?;
        rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
        Ok(FeedResponse::new(
            rows.into_iter().map(row_to_database).collect(),
        ))
    }

    async fn replace_database(&self, database: CosmosDatabase) -> CosmosResult<CosmosDatabase> {
        let existing: Option<DatabaseRow> = self
            .db
            .select((DATABASE_TABLE, encode_key(&database.id)))
            .await
            .map_err(surreal_err)?;
        let existing = existing.ok_or_else(|| CosmosError::not_found("database", &database.id))?;
        let updated = CosmosDatabase {
            id: database.id.clone(),
            rid: existing.rid,
            etag: etag(),
            timestamp: chrono::Utc::now().timestamp(),
            max_throughput: database.max_throughput,
        };
        let _: Option<DatabaseRow> = self
            .db
            .upsert((DATABASE_TABLE, encode_key(&updated.id)))
            .content(database_to_row(&updated))
            .await
            .map_err(surreal_err)?;
        Ok(updated)
    }

    async fn delete_database(&self, id: &str) -> CosmosResult<()> {
        self.ensure_database_exists(id).await?;

        let containers: Vec<ContainerRow> =
            self.db.select(CONTAINER_TABLE).await.map_err(surreal_err)?;
        for c in containers.iter().filter(|c| c.database_id == id) {
            let _: Option<ContainerRow> = self
                .db
                .delete((CONTAINER_TABLE, container_key(&c.database_id, &c.cosmos_id)))
                .await
                .map_err(surreal_err)?;
        }

        let documents: Vec<DocumentRow> =
            self.db.select(DOCUMENT_TABLE).await.map_err(surreal_err)?;
        for d in documents.iter().filter(|d| d.database_id == id) {
            let pk = deserialize_partition_key(&d.partition_key_json);
            let _: Option<DocumentRow> = self
                .db
                .delete((
                    DOCUMENT_TABLE,
                    document_key(&d.database_id, &d.container_id, &d.cosmos_id, &pk),
                ))
                .await
                .map_err(surreal_err)?;
        }

        let permissions: Vec<PermissionRow> = self
            .db
            .select(PERMISSION_TABLE)
            .await
            .map_err(surreal_err)?;
        for p in permissions.iter().filter(|p| p.database_id == id) {
            let _: Option<PermissionRow> = self
                .db
                .delete((
                    PERMISSION_TABLE,
                    permission_key(&p.database_id, &p.user_id, &p.cosmos_id),
                ))
                .await
                .map_err(surreal_err)?;
        }

        let users: Vec<UserRow> = self.db.select(USER_TABLE).await.map_err(surreal_err)?;
        for u in users.iter().filter(|u| u.database_id == id) {
            let _: Option<UserRow> = self
                .db
                .delete((USER_TABLE, user_key(&u.database_id, &u.cosmos_id)))
                .await
                .map_err(surreal_err)?;
        }

        let _: Option<DatabaseRow> = self
            .db
            .delete((DATABASE_TABLE, encode_key(id)))
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    // ---------- Containers ----------

    async fn create_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        self.ensure_database_exists(database_id).await?;
        let existing: Option<ContainerRow> = self
            .db
            .select((CONTAINER_TABLE, container_key(database_id, &container.id)))
            .await
            .map_err(surreal_err)?;
        if existing.is_some() {
            return Err(CosmosError::conflict("container", &container.id));
        }
        let mut container = container;
        container.database_id = database_id.to_string();
        let _: Option<ContainerRow> = self
            .db
            .create((CONTAINER_TABLE, container_key(database_id, &container.id)))
            .content(container_to_row(&container))
            .await
            .map_err(surreal_err)?;
        Ok(container)
    }

    async fn get_container(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<CosmosContainer> {
        self.select_container_row(database_id, container_id).await
    }

    async fn list_containers(
        &self,
        database_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosContainer>> {
        self.ensure_database_exists(database_id).await?;
        let mut rows: Vec<ContainerRow> =
            self.db.select(CONTAINER_TABLE).await.map_err(surreal_err)?;
        rows.retain(|r| r.database_id == database_id);
        rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
        let containers = rows
            .into_iter()
            .map(row_to_container)
            .collect::<CosmosResult<Vec<_>>>()?;
        Ok(FeedResponse::new(containers))
    }

    async fn replace_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        let existing: Option<ContainerRow> = self
            .db
            .select((CONTAINER_TABLE, container_key(database_id, &container.id)))
            .await
            .map_err(surreal_err)?;
        let existing =
            existing.ok_or_else(|| CosmosError::not_found("container", &container.id))?;
        let mut updated = container;
        updated.database_id = database_id.to_string();
        updated.rid = existing.rid;
        updated.etag = etag();
        updated.timestamp = chrono::Utc::now().timestamp();
        let _: Option<ContainerRow> = self
            .db
            .upsert((CONTAINER_TABLE, container_key(database_id, &updated.id)))
            .content(container_to_row(&updated))
            .await
            .map_err(surreal_err)?;
        Ok(updated)
    }

    async fn delete_container(&self, database_id: &str, container_id: &str) -> CosmosResult<()> {
        let existing: Option<ContainerRow> = self
            .db
            .select((CONTAINER_TABLE, container_key(database_id, container_id)))
            .await
            .map_err(surreal_err)?;
        if existing.is_none() {
            return Err(CosmosError::not_found("container", container_id));
        }

        let documents: Vec<DocumentRow> =
            self.db.select(DOCUMENT_TABLE).await.map_err(surreal_err)?;
        for d in documents
            .iter()
            .filter(|d| d.database_id == database_id && d.container_id == container_id)
        {
            let pk = deserialize_partition_key(&d.partition_key_json);
            let _: Option<DocumentRow> = self
                .db
                .delete((
                    DOCUMENT_TABLE,
                    document_key(database_id, container_id, &d.cosmos_id, &pk),
                ))
                .await
                .map_err(surreal_err)?;
        }

        let _: Option<ContainerRow> = self
            .db
            .delete((CONTAINER_TABLE, container_key(database_id, container_id)))
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    // ---------- Documents ----------

    async fn create_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let container = self.select_container_row(database_id, container_id).await?;
        let id = require_id(&document)?;
        enforce_document_size(&document)?;
        let pk = extract_partition_key(&container, &document);
        if self
            .select_document_row(database_id, container_id, &id, &pk)
            .await?
            .is_some()
        {
            return Err(CosmosError::conflict("document", &id));
        }
        let mut doc = CosmosDocument::new(database_id, container_id, id, pk, document);
        doc.lsn = self.next_lsn().await?;
        doc.is_indexed = is_indexed.unwrap_or(true);
        self.upsert_document_row(&doc).await?;
        self.record_change(&doc, ChangeType::Create, None).await?;
        Ok(doc)
    }

    async fn read_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<CosmosDocument> {
        self.select_document_row(database_id, container_id, document_id, partition_key)
            .await?
            .ok_or_else(|| CosmosError::not_found("document", document_id))
    }

    async fn replace_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        document: JsonObject,
        if_match: Option<&str>,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let container = self.select_container_row(database_id, container_id).await?;
        enforce_document_size(&document)?;
        let pk = extract_partition_key(&container, &document);
        let existing = self
            .select_document_row(database_id, container_id, document_id, &pk)
            .await?
            .ok_or_else(|| CosmosError::not_found("document", document_id))?;
        if let Some(expected) = if_match {
            if existing.etag != expected {
                return Err(CosmosError::precondition_failed(
                    "ETag does not match for replace.",
                ));
            }
        }
        let mut doc = CosmosDocument::new(database_id, container_id, document_id, pk, document);
        doc.rid = existing.rid.clone();
        doc.lsn = self.next_lsn().await?;
        doc.etag = etag();
        doc.is_indexed = is_indexed.unwrap_or(true);
        self.upsert_document_row(&doc).await?;
        self.record_change(&doc, ChangeType::Replace, Some(&existing))
            .await?;
        Ok(doc)
    }

    async fn upsert_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let id = require_id(&document)?;
        match self
            .replace_document(
                database_id,
                container_id,
                &id,
                document.clone(),
                None,
                is_indexed,
            )
            .await
        {
            Ok(doc) => Ok(doc),
            Err(e) if e.status_code == 404 => {
                self.create_document(database_id, container_id, document, is_indexed)
                    .await
            }
            Err(e) => Err(e),
        }
    }

    #[allow(clippy::too_many_arguments)]
    async fn patch_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
        operations: &[PatchOperation],
        if_match: Option<&str>,
        _condition: Option<&str>,
    ) -> CosmosResult<CosmosDocument> {
        let existing = self
            .select_document_row(database_id, container_id, document_id, partition_key)
            .await?
            .ok_or_else(|| CosmosError::not_found("document", document_id))?;
        if let Some(expected) = if_match {
            if existing.etag != expected {
                return Err(CosmosError::precondition_failed(
                    "ETag does not match for patch.",
                ));
            }
        }
        let mut body = existing.body.clone();
        for op in operations {
            apply_patch(&mut body, op)?;
        }
        let mut doc = existing.clone();
        doc.body = body;
        doc.lsn = self.next_lsn().await?;
        doc.etag = etag();
        self.upsert_document_row(&doc).await?;
        self.record_change(&doc, ChangeType::Replace, Some(&existing))
            .await?;
        Ok(doc)
    }

    async fn delete_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<()> {
        let mut existing = self
            .select_document_row(database_id, container_id, document_id, partition_key)
            .await?
            .ok_or_else(|| CosmosError::not_found("document", document_id))?;
        let _: Option<DocumentRow> = self
            .db
            .delete((
                DOCUMENT_TABLE,
                document_key(database_id, container_id, document_id, partition_key),
            ))
            .await
            .map_err(surreal_err)?;
        existing.lsn = self.next_lsn().await?;
        self.record_change(&existing, ChangeType::Delete, None)
            .await?;
        Ok(())
    }

    async fn empty_container(&self, database_id: &str, container_id: &str) -> CosmosResult<usize> {
        let existing: Option<ContainerRow> = self
            .db
            .select((CONTAINER_TABLE, container_key(database_id, container_id)))
            .await
            .map_err(surreal_err)?;
        if existing.is_none() {
            return Err(CosmosError::not_found("container", container_id));
        }
        let documents: Vec<DocumentRow> =
            self.db.select(DOCUMENT_TABLE).await.map_err(surreal_err)?;
        let mut count = 0;
        for d in documents
            .iter()
            .filter(|d| d.database_id == database_id && d.container_id == container_id)
        {
            let pk = deserialize_partition_key(&d.partition_key_json);
            let _: Option<DocumentRow> = self
                .db
                .delete((
                    DOCUMENT_TABLE,
                    document_key(database_id, container_id, &d.cosmos_id, &pk),
                ))
                .await
                .map_err(surreal_err)?;
            count += 1;
        }
        Ok(count)
    }

    async fn get_global_lsn(&self) -> CosmosResult<i64> {
        Ok(self.next_lsn.load(Ordering::SeqCst))
    }

    // ---------- Batch & bulk ----------

    async fn execute_batch(
        &self,
        database_id: &str,
        container_id: &str,
        partition_key: &PartitionKeyValue,
        operations: &[BatchOperationRequest],
    ) -> CosmosResult<Vec<BatchOperationResponse>> {
        let mut responses = Vec::with_capacity(operations.len());
        for op in operations {
            let result = match op.operation_type {
                BatchOperationType::Create => {
                    let body = op.resource_body.clone().unwrap_or_default();
                    self.create_document(database_id, container_id, body, None)
                        .await
                }
                BatchOperationType::Upsert => {
                    let body = op.resource_body.clone().unwrap_or_default();
                    self.upsert_document(database_id, container_id, body, None)
                        .await
                }
                BatchOperationType::Read => {
                    let id = op.id.clone().unwrap_or_default();
                    self.read_document(database_id, container_id, &id, partition_key)
                        .await
                }
                BatchOperationType::Replace => {
                    let id = op.id.clone().unwrap_or_default();
                    let body = op.resource_body.clone().unwrap_or_default();
                    self.replace_document(
                        database_id,
                        container_id,
                        &id,
                        body,
                        op.if_match.as_deref(),
                        None,
                    )
                    .await
                }
                BatchOperationType::Delete => {
                    let id = op.id.clone().unwrap_or_default();
                    self.delete_document(database_id, container_id, &id, partition_key)
                        .await
                        .map(|_| {
                            CosmosDocument::new(
                                database_id,
                                container_id,
                                id,
                                partition_key.clone(),
                                JsonObject::new(),
                            )
                        })
                }
                BatchOperationType::Patch => {
                    return Err(CosmosError::bad_request(
                        "Patch is not yet supported in batch operations.",
                    ))
                }
            };
            match result {
                Ok(doc) => responses.push(BatchOperationResponse {
                    status_code: 200,
                    resource_body: Some(doc.to_response_body()),
                    etag: Some(doc.etag),
                    request_charge: 1.0,
                    retry_after_ms: None,
                }),
                Err(e) => responses.push(BatchOperationResponse {
                    status_code: e.status_code,
                    resource_body: None,
                    etag: None,
                    request_charge: 0.0,
                    retry_after_ms: e.retry_after_ms.map(|v| v as i32),
                }),
            }
        }
        Ok(responses)
    }

    async fn read_many_documents(
        &self,
        database_id: &str,
        container_id: &str,
        items: &[(String, PartitionKeyValue)],
    ) -> CosmosResult<FeedResponse<CosmosDocument>> {
        let mut found = Vec::new();
        for (id, pk) in items {
            if let Ok(doc) = self.read_document(database_id, container_id, id, pk).await {
                found.push(doc);
            }
        }
        Ok(FeedResponse::new(found))
    }

    async fn list_documents(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosDocument>> {
        let mut rows: Vec<DocumentRow> =
            self.db.select(DOCUMENT_TABLE).await.map_err(surreal_err)?;
        rows.retain(|r| r.database_id == database_id && r.container_id == container_id);
        rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
        let docs = rows
            .into_iter()
            .map(row_to_document)
            .collect::<CosmosResult<Vec<_>>>()?;
        Ok(FeedResponse::new(docs))
    }

    // ---------- Users ----------

    async fn create_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        let existing: Option<UserRow> = self
            .db
            .select((USER_TABLE, user_key(database_id, user_id)))
            .await
            .map_err(surreal_err)?;
        if existing.is_some() {
            return Err(CosmosError::conflict("user", user_id));
        }
        let user = CosmosUser::new(database_id, user_id);
        let _: Option<UserRow> = self
            .db
            .create((USER_TABLE, user_key(database_id, user_id)))
            .content(user_to_row(&user))
            .await
            .map_err(surreal_err)?;
        Ok(user)
    }

    async fn get_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        let row: Option<UserRow> = self
            .db
            .select((USER_TABLE, user_key(database_id, user_id)))
            .await
            .map_err(surreal_err)?;
        row.map(row_to_user)
            .ok_or_else(|| CosmosError::not_found("user", user_id))
    }

    async fn list_users(&self, database_id: &str) -> CosmosResult<FeedResponse<CosmosUser>> {
        let mut rows: Vec<UserRow> = self.db.select(USER_TABLE).await.map_err(surreal_err)?;
        rows.retain(|r| r.database_id == database_id);
        rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
        Ok(FeedResponse::new(
            rows.into_iter().map(row_to_user).collect(),
        ))
    }

    async fn replace_user(&self, database_id: &str, user: CosmosUser) -> CosmosResult<CosmosUser> {
        let existing: Option<UserRow> = self
            .db
            .select((USER_TABLE, user_key(database_id, &user.id)))
            .await
            .map_err(surreal_err)?;
        if existing.is_none() {
            return Err(CosmosError::not_found("user", &user.id));
        }
        let mut user = user;
        user.database_id = database_id.to_string();
        let _: Option<UserRow> = self
            .db
            .upsert((USER_TABLE, user_key(database_id, &user.id)))
            .content(user_to_row(&user))
            .await
            .map_err(surreal_err)?;
        Ok(user)
    }

    async fn delete_user(&self, database_id: &str, user_id: &str) -> CosmosResult<()> {
        let deleted: Option<UserRow> = self
            .db
            .delete((USER_TABLE, user_key(database_id, user_id)))
            .await
            .map_err(surreal_err)?;
        if deleted.is_none() {
            return Err(CosmosError::not_found("user", user_id));
        }
        // Cascade delete the user's permissions.
        let permissions: Vec<PermissionRow> = self
            .db
            .select(PERMISSION_TABLE)
            .await
            .map_err(surreal_err)?;
        for p in permissions
            .iter()
            .filter(|p| p.database_id == database_id && p.user_id == user_id)
        {
            let _: Option<PermissionRow> = self
                .db
                .delete((
                    PERMISSION_TABLE,
                    permission_key(database_id, user_id, &p.cosmos_id),
                ))
                .await
                .map_err(surreal_err)?;
        }
        Ok(())
    }

    // ---------- Permissions ----------

    async fn create_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        let user: Option<UserRow> = self
            .db
            .select((USER_TABLE, user_key(database_id, user_id)))
            .await
            .map_err(surreal_err)?;
        if user.is_none() {
            return Err(CosmosError::not_found("user", user_id));
        }
        let existing: Option<PermissionRow> = self
            .db
            .select((
                PERMISSION_TABLE,
                permission_key(database_id, user_id, &permission.id),
            ))
            .await
            .map_err(surreal_err)?;
        if existing.is_some() {
            return Err(CosmosError::conflict("permission", &permission.id));
        }
        let _: Option<PermissionRow> = self
            .db
            .create((
                PERMISSION_TABLE,
                permission_key(database_id, user_id, &permission.id),
            ))
            .content(permission_to_row(database_id, user_id, &permission))
            .await
            .map_err(surreal_err)?;
        Ok(permission)
    }

    async fn get_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<CosmosPermission> {
        let row: Option<PermissionRow> = self
            .db
            .select((
                PERMISSION_TABLE,
                permission_key(database_id, user_id, permission_id),
            ))
            .await
            .map_err(surreal_err)?;
        row.map(row_to_permission)
            .ok_or_else(|| CosmosError::not_found("permission", permission_id))
    }

    async fn list_permissions(
        &self,
        database_id: &str,
        user_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosPermission>> {
        let mut rows: Vec<PermissionRow> = self
            .db
            .select(PERMISSION_TABLE)
            .await
            .map_err(surreal_err)?;
        rows.retain(|r| r.database_id == database_id && r.user_id == user_id);
        rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
        Ok(FeedResponse::new(
            rows.into_iter().map(row_to_permission).collect(),
        ))
    }

    async fn replace_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        let existing: Option<PermissionRow> = self
            .db
            .select((
                PERMISSION_TABLE,
                permission_key(database_id, user_id, &permission.id),
            ))
            .await
            .map_err(surreal_err)?;
        if existing.is_none() {
            return Err(CosmosError::not_found("permission", &permission.id));
        }
        let _: Option<PermissionRow> = self
            .db
            .upsert((
                PERMISSION_TABLE,
                permission_key(database_id, user_id, &permission.id),
            ))
            .content(permission_to_row(database_id, user_id, &permission))
            .await
            .map_err(surreal_err)?;
        Ok(permission)
    }

    async fn delete_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<()> {
        let deleted: Option<PermissionRow> = self
            .db
            .delete((
                PERMISSION_TABLE,
                permission_key(database_id, user_id, permission_id),
            ))
            .await
            .map_err(surreal_err)?;
        if deleted.is_none() {
            return Err(CosmosError::not_found("permission", permission_id));
        }
        Ok(())
    }

    // ---------- Offers ----------

    async fn get_offer(&self, offer_id: &str) -> CosmosResult<CosmosOffer> {
        let row: Option<OfferRow> = self
            .db
            .select((OFFER_TABLE, encode_key(offer_id)))
            .await
            .map_err(surreal_err)?;
        row.map(row_to_offer)
            .ok_or_else(|| CosmosError::not_found("offer", offer_id))
    }

    async fn list_offers(&self) -> CosmosResult<FeedResponse<CosmosOffer>> {
        let mut rows: Vec<OfferRow> = self.db.select(OFFER_TABLE).await.map_err(surreal_err)?;
        rows.sort_by(|a, b| a.cosmos_id.cmp(&b.cosmos_id));
        Ok(FeedResponse::new(
            rows.into_iter().map(row_to_offer).collect(),
        ))
    }

    async fn replace_offer(&self, offer: CosmosOffer) -> CosmosResult<CosmosOffer> {
        let _: Option<OfferRow> = self
            .db
            .upsert((OFFER_TABLE, encode_key(&offer.id)))
            .content(offer_to_row(&offer))
            .await
            .map_err(surreal_err)?;
        Ok(offer)
    }
}

// ─── Change feed provider ────────────────────────────────────────────────────

/// SurrealDB-backed change-feed provider. Ports `SurrealDbChangeFeedProvider`.
pub struct SurrealDbChangeFeedProvider {
    db: Surreal<Db>,
}

impl SurrealDbChangeFeedProvider {
    pub fn new(db: Surreal<Db>) -> Self {
        Self { db }
    }

    async fn load_items(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<Vec<ChangeFeedItem>> {
        let mut rows: Vec<ChangeRow> = self
            .db
            .select(CHANGEFEED_TABLE)
            .await
            .map_err(surreal_err)?;
        rows.retain(|r| r.database_id == database_id && r.container_id == container_id);
        rows.sort_by_key(|r| r.lsn);
        rows.into_iter().map(change_row_to_item).collect()
    }
}

#[async_trait]
impl ChangeFeedProvider for SurrealDbChangeFeedProvider {
    async fn read_change_feed(
        &self,
        database_id: &str,
        container_id: &str,
        options: ChangeFeedOptions,
    ) -> CosmosResult<FeedResponse<ChangeFeedItem>> {
        let items = self.load_items(database_id, container_id).await?;
        if items.is_empty() {
            let mut feed = FeedResponse::new(Vec::new());
            feed.continuation_token = Some("0".to_string());
            return Ok(feed);
        }
        let start_lsn = resolve_start_lsn(&items, &options);
        let (result, last_lsn) = page_items(&items, start_lsn, &options);
        let mut feed = FeedResponse::new(result);
        feed.continuation_token = Some(last_lsn.to_string());
        Ok(feed)
    }

    async fn record_change(
        &self,
        database_id: &str,
        container_id: &str,
        document: &CosmosDocument,
        change_type: ChangeType,
        previous_image: Option<&CosmosDocument>,
    ) -> CosmosResult<()> {
        let row = ChangeRow {
            database_id: database_id.to_string(),
            container_id: container_id.to_string(),
            document_id: document.id.clone(),
            lsn: document.lsn,
            change_type: change_type_code(change_type),
            body_json: to_json(&document.body),
            previous_image_json: previous_image.map(|p| to_json(&p.body)),
            partition_key_json: serialize_partition_key(&document.partition_key),
            timestamp: Utc::now().timestamp_millis(),
        };
        let key = format!(
            "{}:{}:{}",
            encode_key(database_id),
            encode_key(container_id),
            encode_key(&document.lsn.to_string())
        );
        let _: Option<ChangeRow> = self
            .db
            .upsert((CHANGEFEED_TABLE, key))
            .content(row)
            .await
            .map_err(surreal_err)?;
        Ok(())
    }

    async fn trim(&self, retention: std::time::Duration) -> CosmosResult<()> {
        let cutoff = Utc::now().timestamp_millis() - retention.as_millis() as i64;
        let rows: Vec<ChangeRow> = self
            .db
            .select(CHANGEFEED_TABLE)
            .await
            .map_err(surreal_err)?;
        for r in rows.iter().filter(|r| r.timestamp < cutoff) {
            let key = format!(
                "{}:{}:{}",
                encode_key(&r.database_id),
                encode_key(&r.container_id),
                encode_key(&r.lsn.to_string())
            );
            let _: Option<ChangeRow> = self
                .db
                .delete((CHANGEFEED_TABLE, key))
                .await
                .map_err(surreal_err)?;
        }
        Ok(())
    }
}

/// Resolves the starting LSN cursor (mirrors the .NET provider): continuation
/// token, else start-time, else the latest LSN (unless start-from-beginning).
fn resolve_start_lsn(items: &[ChangeFeedItem], options: &ChangeFeedOptions) -> i64 {
    if let Some(token) = &options.continuation_token {
        if let Ok(lsn) = token.parse::<i64>() {
            return lsn;
        }
    }
    if let Some(start) = options.start_time {
        let start_ms = start.timestamp_millis();
        return items
            .iter()
            .filter(|i| i.timestamp.timestamp_millis() >= start_ms)
            .map(|i| i.lsn)
            .min()
            .unwrap_or(0);
    }
    if !options.start_from_beginning {
        return items.last().map(|i| i.lsn).unwrap_or(0);
    }
    0
}

// ─── Conversions ─────────────────────────────────────────────────────────────

fn change_row_to_item(row: ChangeRow) -> CosmosResult<ChangeFeedItem> {
    let body: JsonObject = from_json(&row.body_json)?;
    let pk = deserialize_partition_key(&row.partition_key_json);
    let mut doc = CosmosDocument::new(
        &row.database_id,
        &row.container_id,
        &row.document_id,
        pk.clone(),
        body,
    );
    doc.lsn = row.lsn;
    let previous_image = match &row.previous_image_json {
        Some(json) => {
            let prev_body: JsonObject = from_json(json)?;
            let mut prev = CosmosDocument::new(
                &row.database_id,
                &row.container_id,
                &row.document_id,
                pk,
                prev_body,
            );
            prev.lsn = row.lsn;
            Some(prev)
        }
        None => None,
    };
    let timestamp = Utc
        .timestamp_millis_opt(row.timestamp)
        .single()
        .unwrap_or_else(Utc::now);
    let mut item = ChangeFeedItem::new(doc, row.lsn, change_type_from_code(row.change_type));
    item.previous_image = previous_image;
    item.timestamp = timestamp;
    Ok(item)
}

fn database_to_row(db: &CosmosDatabase) -> DatabaseRow {
    DatabaseRow {
        cosmos_id: db.id.clone(),
        rid: db.rid.clone(),
        etag: db.etag.clone(),
        timestamp: db.timestamp,
        max_throughput: db.max_throughput,
    }
}

fn row_to_database(row: DatabaseRow) -> CosmosDatabase {
    CosmosDatabase {
        id: row.cosmos_id,
        rid: row.rid,
        etag: row.etag,
        timestamp: row.timestamp,
        max_throughput: row.max_throughput,
    }
}

fn container_to_row(c: &CosmosContainer) -> ContainerRow {
    ContainerRow {
        cosmos_id: c.id.clone(),
        database_id: c.database_id.clone(),
        rid: c.rid.clone(),
        etag: c.etag.clone(),
        timestamp: c.timestamp,
        partition_key_json: to_json(&c.partition_key),
        indexing_policy_json: to_json(&c.indexing_policy),
        default_ttl: c.default_time_to_live,
        max_throughput: c.max_throughput,
        unique_key_policy_json: c.unique_key_policy.as_ref().map(to_json),
        conflict_resolution_policy_json: c.conflict_resolution_policy.as_ref().map(to_json),
        vector_embedding_policy_json: c.vector_embedding_policy.as_ref().map(to_json),
    }
}

fn row_to_container(row: ContainerRow) -> CosmosResult<CosmosContainer> {
    Ok(CosmosContainer {
        id: row.cosmos_id,
        rid: row.rid,
        self_link: String::new(),
        etag: row.etag,
        timestamp: row.timestamp,
        database_id: row.database_id,
        partition_key: from_json(&row.partition_key_json)?,
        indexing_policy: from_json(&row.indexing_policy_json)?,
        default_time_to_live: row.default_ttl,
        max_throughput: row.max_throughput,
        unique_key_policy: from_json_opt(row.unique_key_policy_json)?,
        conflict_resolution_policy: from_json_opt(row.conflict_resolution_policy_json)?,
        vector_embedding_policy: from_json_opt(row.vector_embedding_policy_json)?,
    })
}

fn document_to_row(doc: &CosmosDocument) -> DocumentRow {
    DocumentRow {
        cosmos_id: doc.id.clone(),
        database_id: doc.database_id.clone(),
        container_id: doc.container_id.clone(),
        rid: doc.rid.clone(),
        etag: doc.etag.clone(),
        timestamp: doc.timestamp,
        partition_key_json: serialize_partition_key(&doc.partition_key),
        body_json: to_json(&doc.body),
        ttl: doc.time_to_live,
        lsn: doc.lsn,
        is_indexed: doc.is_indexed,
    }
}

fn row_to_document(row: DocumentRow) -> CosmosResult<CosmosDocument> {
    let body: JsonObject = from_json(&row.body_json)?;
    Ok(CosmosDocument {
        id: row.cosmos_id,
        rid: row.rid,
        self_link: String::new(),
        etag: row.etag,
        timestamp: row.timestamp,
        database_id: row.database_id,
        container_id: row.container_id,
        partition_key: deserialize_partition_key(&row.partition_key_json),
        body,
        time_to_live: row.ttl,
        lsn: row.lsn,
        is_indexed: row.is_indexed,
    })
}

fn user_to_row(user: &CosmosUser) -> UserRow {
    UserRow {
        cosmos_id: user.id.clone(),
        database_id: user.database_id.clone(),
        rid: user.rid.clone(),
        etag: user.etag.clone(),
        timestamp: user.timestamp,
    }
}

fn row_to_user(row: UserRow) -> CosmosUser {
    CosmosUser {
        id: row.cosmos_id,
        rid: row.rid,
        self_link: String::new(),
        etag: row.etag,
        timestamp: row.timestamp,
        database_id: row.database_id,
    }
}

fn permission_to_row(database_id: &str, user_id: &str, p: &CosmosPermission) -> PermissionRow {
    PermissionRow {
        cosmos_id: p.id.clone(),
        database_id: database_id.to_string(),
        user_id: user_id.to_string(),
        rid: p.rid.clone(),
        etag: p.etag.clone(),
        timestamp: p.timestamp,
        permission_mode: permission_mode_str(p.permission_mode).to_string(),
        resource: p.resource.clone(),
        token: p.token.clone(),
    }
}

fn row_to_permission(row: PermissionRow) -> CosmosPermission {
    CosmosPermission {
        id: row.cosmos_id,
        rid: row.rid,
        self_link: String::new(),
        etag: row.etag,
        timestamp: row.timestamp,
        database_id: row.database_id,
        user_id: row.user_id,
        permission_mode: if row.permission_mode == "All" {
            PermissionMode::All
        } else {
            PermissionMode::Read
        },
        resource: row.resource,
        token: row.token,
    }
}

fn offer_to_row(offer: &CosmosOffer) -> OfferRow {
    OfferRow {
        cosmos_id: offer.id.clone(),
        rid: offer.rid.clone(),
        etag: offer.etag.clone(),
        timestamp: offer.timestamp,
        offer_version: offer.offer_version.clone(),
        offer_type: offer.offer_type.clone(),
        offer_throughput: offer.content.offer_throughput,
        resource: offer.resource.clone(),
        offer_resource_id: offer.offer_resource_id.clone(),
    }
}

fn row_to_offer(row: OfferRow) -> CosmosOffer {
    CosmosOffer {
        id: row.cosmos_id,
        rid: row.rid,
        etag: row.etag,
        timestamp: row.timestamp,
        offer_version: row.offer_version,
        offer_type: row.offer_type,
        content: OfferContent {
            offer_throughput: row.offer_throughput,
        },
        resource: row.resource,
        offer_resource_id: row.offer_resource_id,
    }
}

fn sproc_to_row(s: &StoredProcedure) -> SprocRow {
    SprocRow {
        cosmos_id: s.id.clone(),
        database_id: s.database_id.clone(),
        container_id: s.container_id.clone(),
        rid: s.rid.clone(),
        etag: s.etag.clone(),
        timestamp: s.timestamp,
        body: s.body.clone(),
    }
}

fn trigger_to_row(t: &Trigger) -> TriggerRow {
    TriggerRow {
        cosmos_id: t.id.clone(),
        database_id: t.database_id.clone(),
        container_id: t.container_id.clone(),
        rid: t.rid.clone(),
        etag: t.etag.clone(),
        timestamp: t.timestamp,
        body: t.body.clone(),
        trigger_type: t.trigger_type.as_int(),
        trigger_operation: t.trigger_operation.as_int(),
    }
}

fn udf_to_row(u: &UserDefinedFunction) -> UdfRow {
    UdfRow {
        cosmos_id: u.id.clone(),
        database_id: u.database_id.clone(),
        container_id: u.container_id.clone(),
        rid: u.rid.clone(),
        etag: u.etag.clone(),
        timestamp: u.timestamp,
        body: u.body.clone(),
    }
}

fn row_to_sproc(row: SprocRow) -> StoredProcedure {
    StoredProcedure {
        self_link: format!(
            "dbs/{}/colls/{}/sprocs/{}/",
            row.database_id, row.container_id, row.cosmos_id
        ),
        id: row.cosmos_id,
        database_id: row.database_id,
        container_id: row.container_id,
        rid: row.rid,
        etag: row.etag,
        timestamp: row.timestamp,
        body: row.body,
    }
}

fn row_to_trigger(row: TriggerRow) -> Trigger {
    Trigger {
        self_link: format!(
            "dbs/{}/colls/{}/triggers/{}/",
            row.database_id, row.container_id, row.cosmos_id
        ),
        id: row.cosmos_id,
        database_id: row.database_id,
        container_id: row.container_id,
        rid: row.rid,
        etag: row.etag,
        timestamp: row.timestamp,
        body: row.body,
        trigger_type: trigger_type_from_int(row.trigger_type),
        trigger_operation: trigger_operation_from_int(row.trigger_operation),
    }
}

fn row_to_udf(row: UdfRow) -> UserDefinedFunction {
    UserDefinedFunction {
        self_link: format!(
            "dbs/{}/colls/{}/udfs/{}/",
            row.database_id, row.container_id, row.cosmos_id
        ),
        id: row.cosmos_id,
        database_id: row.database_id,
        container_id: row.container_id,
        rid: row.rid,
        etag: row.etag,
        timestamp: row.timestamp,
        body: row.body,
    }
}

fn trigger_type_from_int(value: i32) -> TriggerType {
    match value {
        1 => TriggerType::Post,
        _ => TriggerType::Pre,
    }
}

fn trigger_operation_from_int(value: i32) -> TriggerOperation {
    match value {
        1 => TriggerOperation::Create,
        2 => TriggerOperation::Replace,
        3 => TriggerOperation::Delete,
        _ => TriggerOperation::All,
    }
}

// ─── Helpers ─────────────────────────────────────────────────────────────────

fn permission_mode_str(mode: PermissionMode) -> &'static str {
    match mode {
        PermissionMode::All => "All",
        PermissionMode::Read => "Read",
    }
}

fn change_type_code(change_type: ChangeType) -> i64 {
    match change_type {
        ChangeType::Create => 0,
        ChangeType::Replace => 1,
        ChangeType::Delete => 2,
    }
}

fn change_type_from_code(code: i64) -> ChangeType {
    match code {
        0 => ChangeType::Create,
        2 => ChangeType::Delete,
        _ => ChangeType::Replace,
    }
}

fn enforce_document_size(document: &JsonObject) -> CosmosResult<()> {
    let size = serde_json::to_vec(document).map(|v| v.len()).unwrap_or(0);
    if size > MAX_DOCUMENT_SIZE_BYTES {
        return Err(CosmosError::bad_request(format!(
            "Document size {size} exceeds the maximum of {MAX_DOCUMENT_SIZE_BYTES} bytes."
        )));
    }
    Ok(())
}

fn to_json<T: Serialize>(value: &T) -> String {
    serde_json::to_string(value).unwrap_or_else(|_| "null".to_string())
}

fn from_json<T: serde::de::DeserializeOwned>(json: &str) -> CosmosResult<T> {
    serde_json::from_str(json)
        .map_err(|e| CosmosError::internal_server_error(format!("corrupt persisted json: {e}")))
}

fn from_json_opt<T: serde::de::DeserializeOwned>(json: Option<String>) -> CosmosResult<Option<T>> {
    match json {
        Some(s) if !s.is_empty() => Ok(Some(from_json(&s)?)),
        _ => Ok(None),
    }
}

fn surreal_err(e: surrealdb::Error) -> CosmosError {
    CosmosError::internal_server_error(format!("surrealdb error: {e}"))
}

fn encode_key(value: &str) -> String {
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(value.as_bytes())
}

fn container_key(database_id: &str, container_id: &str) -> String {
    format!("{}:{}", encode_key(database_id), encode_key(container_id))
}

fn document_key(
    database_id: &str,
    container_id: &str,
    document_id: &str,
    pk: &PartitionKeyValue,
) -> String {
    format!(
        "{}:{}:{}:{}",
        encode_key(database_id),
        encode_key(container_id),
        encode_key(&pk.to_header_string()),
        encode_key(document_id)
    )
}

fn user_key(database_id: &str, user_id: &str) -> String {
    format!("{}:{}", encode_key(database_id), encode_key(user_id))
}

fn permission_key(database_id: &str, user_id: &str, permission_id: &str) -> String {
    format!(
        "{}:{}:{}",
        encode_key(database_id),
        encode_key(user_id),
        encode_key(permission_id)
    )
}

/// A cheap unique-ish suffix for isolating test databases (no uuid dep here).
fn uuid_like() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    format!("{nanos}-{:?}", std::thread::current().id())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn body(id: &str, pk: &str) -> JsonObject {
        json!({ "id": id, "pk": pk, "value": 1 })
            .as_object()
            .unwrap()
            .clone()
    }

    async fn seed(store: &SurrealDbDocumentStore) -> CosmosContainer {
        store.create_database("db1").await.unwrap();
        let container = CosmosContainer::new(
            "db1",
            "coll1",
            PartitionKeyDefinition::new(vec!["/pk".to_string()]),
        );
        store
            .create_container("db1", container.clone())
            .await
            .unwrap()
    }

    #[tokio::test]
    async fn create_read_delete_roundtrip() {
        let store = SurrealDbDocumentStore::in_memory().await.unwrap();
        seed(&store).await;

        let created = store
            .create_document("db1", "coll1", body("doc1", "tenant-a"), None)
            .await
            .unwrap();
        assert_eq!(created.id, "doc1");
        assert_eq!(created.lsn, 1);

        let pk = PartitionKeyValue::single(json!("tenant-a"));
        let read = store
            .read_document("db1", "coll1", "doc1", &pk)
            .await
            .unwrap();
        assert_eq!(read.body.get("value"), Some(&json!(1)));

        store
            .delete_document("db1", "coll1", "doc1", &pk)
            .await
            .unwrap();
        let err = store
            .read_document("db1", "coll1", "doc1", &pk)
            .await
            .unwrap_err();
        assert_eq!(err.status_code, 404);
    }

    #[tokio::test]
    async fn conflict_and_partition_key_mismatch() {
        let store = SurrealDbDocumentStore::in_memory().await.unwrap();
        seed(&store).await;
        store
            .create_document("db1", "coll1", body("doc1", "tenant-a"), None)
            .await
            .unwrap();
        let conflict = store
            .create_document("db1", "coll1", body("doc1", "tenant-a"), None)
            .await
            .unwrap_err();
        assert_eq!(conflict.status_code, 409);

        let wrong_pk = PartitionKeyValue::single(json!("tenant-b"));
        let err = store
            .read_document("db1", "coll1", "doc1", &wrong_pk)
            .await
            .unwrap_err();
        assert_eq!(err.status_code, 404);
    }

    #[tokio::test]
    async fn upsert_patch_and_lsn_progression() {
        let store = SurrealDbDocumentStore::in_memory().await.unwrap();
        seed(&store).await;
        let pk = PartitionKeyValue::single(json!("tenant-a"));

        store
            .upsert_document("db1", "coll1", body("doc1", "tenant-a"), None)
            .await
            .unwrap();
        let updated = store
            .upsert_document("db1", "coll1", body("doc1", "tenant-a"), None)
            .await
            .unwrap();
        assert!(updated.lsn >= 2);

        let ops = vec![PatchOperation {
            op: "set".to_string(),
            path: "/value".to_string(),
            value: Some(json!(42)),
            from: None,
        }];
        let patched = store
            .patch_document("db1", "coll1", "doc1", &pk, &ops, None, None)
            .await
            .unwrap();
        assert_eq!(patched.body.get("value"), Some(&json!(42)));
        assert!(store.get_global_lsn().await.unwrap() >= patched.lsn);
    }

    #[tokio::test]
    async fn persistence_across_reopen() {
        let dir = std::env::current_dir()
            .unwrap()
            .join("target")
            .join("cosmos-surreal-tests")
            .join(uuid_like());
        {
            let store = SurrealDbDocumentStore::open(&dir).await.unwrap();
            seed(&store).await;
            store
                .create_document("db1", "coll1", body("doc1", "tenant-a"), None)
                .await
                .unwrap();
        }
        {
            let store = SurrealDbDocumentStore::open(&dir).await.unwrap();
            let pk = PartitionKeyValue::single(json!("tenant-a"));
            let read = store
                .read_document("db1", "coll1", "doc1", &pk)
                .await
                .unwrap();
            assert_eq!(read.id, "doc1");
        }
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[tokio::test]
    async fn change_feed_records_document_writes() {
        let store = SurrealDbDocumentStore::in_memory().await.unwrap();
        seed(&store).await;
        store
            .create_document("db1", "coll1", body("doc1", "tenant-a"), None)
            .await
            .unwrap();
        store
            .create_document("db1", "coll1", body("doc2", "tenant-a"), None)
            .await
            .unwrap();

        let feed = store.change_feed();
        let options = ChangeFeedOptions {
            start_from_beginning: true,
            ..Default::default()
        };
        let response = feed
            .read_change_feed("db1", "coll1", options)
            .await
            .unwrap();
        assert_eq!(response.resources.len(), 2);
        assert_eq!(response.resources[0].document.id, "doc1");
        assert_eq!(response.resources[1].document.id, "doc2");
    }
}
