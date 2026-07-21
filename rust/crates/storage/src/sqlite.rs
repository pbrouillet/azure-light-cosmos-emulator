//! SQLite-backed `DocumentStore`. Ports `SqliteDocumentStore` /
//! `SqliteConnectionManager` from `src/Storage/Sqlite/*`.
//!
//! Uses a single `rusqlite` connection guarded by a `Mutex` (the emulator is a
//! single-node dev tool, so serialized access is acceptable and keeps the
//! implementation simple and correct). Runs in WAL mode with the schema created
//! on construction. Policies/bodies/partition keys are stored as JSON columns,
//! mirroring the `_json` columns of the .NET schema. The global LSN is persisted
//! in a `meta` key/value table.

use std::path::Path;
use std::sync::Mutex;

use async_trait::async_trait;
use cosmos_core::error::{CosmosError, CosmosResult};
use cosmos_core::ids::etag;
use cosmos_core::models::*;
use cosmos_core::traits::DocumentStore;
use rusqlite::{params, Connection, OptionalExtension, Row};

use crate::common::{apply_patch, extract_partition_key, require_id, serialize_partition_key};

const GLOBAL_LSN_KEY: &str = "global_lsn";

const SCHEMA: &str = r#"
CREATE TABLE IF NOT EXISTS databases (
    id TEXT PRIMARY KEY,
    rid TEXT NOT NULL,
    etag TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    max_throughput INTEGER
);
CREATE TABLE IF NOT EXISTS containers (
    database_id TEXT NOT NULL,
    id TEXT NOT NULL,
    rid TEXT NOT NULL,
    etag TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    partition_key_json TEXT NOT NULL,
    indexing_policy_json TEXT NOT NULL,
    unique_key_policy_json TEXT,
    conflict_resolution_policy_json TEXT,
    vector_embedding_policy_json TEXT,
    default_ttl INTEGER,
    max_throughput INTEGER NOT NULL,
    PRIMARY KEY (database_id, id)
);
CREATE TABLE IF NOT EXISTS documents (
    database_id TEXT NOT NULL,
    container_id TEXT NOT NULL,
    partition_key_json TEXT NOT NULL,
    id TEXT NOT NULL,
    rid TEXT NOT NULL,
    etag TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    body_json TEXT NOT NULL,
    ttl INTEGER,
    lsn INTEGER NOT NULL,
    is_indexed INTEGER NOT NULL,
    PRIMARY KEY (database_id, container_id, partition_key_json, id)
);
CREATE TABLE IF NOT EXISTS users (
    database_id TEXT NOT NULL,
    id TEXT NOT NULL,
    rid TEXT NOT NULL,
    etag TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    PRIMARY KEY (database_id, id)
);
CREATE TABLE IF NOT EXISTS permissions (
    database_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    id TEXT NOT NULL,
    rid TEXT NOT NULL,
    etag TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    permission_mode TEXT NOT NULL,
    resource TEXT NOT NULL,
    token TEXT,
    PRIMARY KEY (database_id, user_id, id)
);
CREATE TABLE IF NOT EXISTS offers (
    id TEXT PRIMARY KEY,
    rid TEXT NOT NULL,
    etag TEXT NOT NULL,
    timestamp INTEGER NOT NULL,
    offer_version TEXT NOT NULL,
    offer_type TEXT NOT NULL,
    offer_throughput INTEGER NOT NULL,
    resource TEXT NOT NULL,
    offer_resource_id TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS meta (
    key TEXT PRIMARY KEY,
    value INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS changefeed (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    database_id TEXT NOT NULL,
    container_id TEXT NOT NULL,
    lsn INTEGER NOT NULL,
    change_type TEXT NOT NULL,
    document_id TEXT NOT NULL,
    partition_key_json TEXT NOT NULL,
    body_json TEXT NOT NULL,
    timestamp INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_changefeed_lsn ON changefeed (database_id, container_id, lsn);
"#;

/// SQLite (persistent) document store.
pub struct SqliteDocumentStore {
    conn: Mutex<Connection>,
}

impl SqliteDocumentStore {
    /// Opens (or creates) the `emulator.db` database inside `data_dir`.
    pub fn open(data_dir: impl AsRef<Path>) -> CosmosResult<Self> {
        let dir = data_dir.as_ref();
        std::fs::create_dir_all(dir)
            .map_err(|e| CosmosError::internal_server_error(e.to_string()))?;
        let path = dir.join("emulator.db");
        let conn = Connection::open(path).map_err(sqlite_err)?;
        Self::from_connection(conn)
    }

    /// Creates a store backed by an in-memory database (used by tests).
    pub fn in_memory() -> CosmosResult<Self> {
        let conn = Connection::open_in_memory().map_err(sqlite_err)?;
        Self::from_connection(conn)
    }

    fn from_connection(conn: Connection) -> CosmosResult<Self> {
        conn.pragma_update(None, "journal_mode", "WAL")
            .map_err(sqlite_err)?;
        conn.pragma_update(None, "synchronous", "NORMAL")
            .map_err(sqlite_err)?;
        conn.execute_batch(SCHEMA).map_err(sqlite_err)?;
        Ok(Self {
            conn: Mutex::new(conn),
        })
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Connection> {
        self.conn.lock().expect("sqlite mutex poisoned")
    }
}

// ─── LSN helpers (mirror SqliteDocumentStore.AllocateNextLsn) ────────────────

fn allocate_next_lsn(conn: &Connection) -> CosmosResult<i64> {
    let meta_lsn: Option<i64> = conn
        .query_row(
            "SELECT value FROM meta WHERE key = ?1",
            params![GLOBAL_LSN_KEY],
            |r| r.get(0),
        )
        .optional()
        .map_err(sqlite_err)?;
    let current = match meta_lsn {
        Some(v) => v,
        None => conn
            .query_row("SELECT COALESCE(MAX(lsn), 0) FROM documents", [], |r| {
                r.get(0)
            })
            .map_err(sqlite_err)?,
    };
    let next = current + 1;
    conn.execute(
        "INSERT OR REPLACE INTO meta (key, value) VALUES (?1, ?2)",
        params![GLOBAL_LSN_KEY, next],
    )
    .map_err(sqlite_err)?;
    Ok(next)
}

fn read_global_lsn(conn: &Connection) -> CosmosResult<i64> {
    let meta_lsn: Option<i64> = conn
        .query_row(
            "SELECT value FROM meta WHERE key = ?1",
            params![GLOBAL_LSN_KEY],
            |r| r.get(0),
        )
        .optional()
        .map_err(sqlite_err)?;
    match meta_lsn {
        Some(v) => Ok(v),
        None => conn
            .query_row("SELECT COALESCE(MAX(lsn), 0) FROM documents", [], |r| {
                r.get(0)
            })
            .map_err(sqlite_err),
    }
}

// ─── Change feed recording ──────────────────────────────────────────────────

fn record_change(
    conn: &Connection,
    doc: &CosmosDocument,
    change_type: ChangeType,
) -> CosmosResult<()> {
    let ct = match change_type {
        ChangeType::Create => "Create",
        ChangeType::Replace => "Replace",
        ChangeType::Delete => "Delete",
    };
    conn.execute(
        "INSERT INTO changefeed \
         (database_id, container_id, lsn, change_type, document_id, partition_key_json, body_json, timestamp) \
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
        params![
            doc.database_id,
            doc.container_id,
            doc.lsn,
            ct,
            doc.id,
            serialize_partition_key(&doc.partition_key),
            serde_json::to_string(&doc.body).unwrap_or_else(|_| "{}".into()),
            doc.timestamp,
        ],
    )
    .map_err(sqlite_err)?;
    Ok(())
}

// ─── Row mappers ─────────────────────────────────────────────────────────────

fn row_to_database(row: &Row) -> rusqlite::Result<CosmosDatabase> {
    Ok(CosmosDatabase {
        id: row.get("id")?,
        rid: row.get("rid")?,
        etag: row.get("etag")?,
        timestamp: row.get("timestamp")?,
        max_throughput: row.get("max_throughput")?,
    })
}

fn row_to_container(row: &Row) -> CosmosResult<CosmosContainer> {
    let partition_key_json: String = row.get("partition_key_json").map_err(sqlite_err)?;
    let indexing_json: String = row.get("indexing_policy_json").map_err(sqlite_err)?;
    let unique_json: Option<String> = row.get("unique_key_policy_json").map_err(sqlite_err)?;
    let conflict_json: Option<String> = row
        .get("conflict_resolution_policy_json")
        .map_err(sqlite_err)?;
    let vector_json: Option<String> = row
        .get("vector_embedding_policy_json")
        .map_err(sqlite_err)?;
    Ok(CosmosContainer {
        id: row.get("id").map_err(sqlite_err)?,
        rid: row.get("rid").map_err(sqlite_err)?,
        self_link: String::new(),
        etag: row.get("etag").map_err(sqlite_err)?,
        timestamp: row.get("timestamp").map_err(sqlite_err)?,
        database_id: row.get("database_id").map_err(sqlite_err)?,
        partition_key: from_json(&partition_key_json)?,
        indexing_policy: from_json(&indexing_json)?,
        default_time_to_live: row.get("default_ttl").map_err(sqlite_err)?,
        max_throughput: row.get("max_throughput").map_err(sqlite_err)?,
        unique_key_policy: from_json_opt(unique_json)?,
        conflict_resolution_policy: from_json_opt(conflict_json)?,
        vector_embedding_policy: from_json_opt(vector_json)?,
    })
}

fn row_to_document(row: &Row) -> CosmosResult<CosmosDocument> {
    let body_json: String = row.get("body_json").map_err(sqlite_err)?;
    let pk_json: String = row.get("partition_key_json").map_err(sqlite_err)?;
    let body: JsonObject = serde_json::from_str(&body_json)
        .map_err(|e| CosmosError::internal_server_error(format!("corrupt document body: {e}")))?;
    Ok(CosmosDocument {
        id: row.get("id").map_err(sqlite_err)?,
        rid: row.get("rid").map_err(sqlite_err)?,
        self_link: String::new(),
        etag: row.get("etag").map_err(sqlite_err)?,
        timestamp: row.get("timestamp").map_err(sqlite_err)?,
        database_id: row.get("database_id").map_err(sqlite_err)?,
        container_id: row.get("container_id").map_err(sqlite_err)?,
        partition_key: crate::common::deserialize_partition_key(&pk_json),
        body,
        time_to_live: row.get("ttl").map_err(sqlite_err)?,
        lsn: row.get("lsn").map_err(sqlite_err)?,
        is_indexed: row.get::<_, i64>("is_indexed").map_err(sqlite_err)? != 0,
    })
}

fn row_to_user(row: &Row) -> rusqlite::Result<CosmosUser> {
    Ok(CosmosUser {
        id: row.get("id")?,
        rid: row.get("rid")?,
        self_link: String::new(),
        etag: row.get("etag")?,
        timestamp: row.get("timestamp")?,
        database_id: row.get("database_id")?,
    })
}

fn row_to_permission(row: &Row) -> CosmosResult<CosmosPermission> {
    let mode: String = row.get("permission_mode").map_err(sqlite_err)?;
    Ok(CosmosPermission {
        id: row.get("id").map_err(sqlite_err)?,
        rid: row.get("rid").map_err(sqlite_err)?,
        self_link: String::new(),
        etag: row.get("etag").map_err(sqlite_err)?,
        timestamp: row.get("timestamp").map_err(sqlite_err)?,
        database_id: row.get("database_id").map_err(sqlite_err)?,
        user_id: row.get("user_id").map_err(sqlite_err)?,
        permission_mode: if mode == "All" {
            PermissionMode::All
        } else {
            PermissionMode::Read
        },
        resource: row.get("resource").map_err(sqlite_err)?,
        token: row.get("token").map_err(sqlite_err)?,
    })
}

fn row_to_offer(row: &Row) -> rusqlite::Result<CosmosOffer> {
    Ok(CosmosOffer {
        id: row.get("id")?,
        rid: row.get("rid")?,
        etag: row.get("etag")?,
        timestamp: row.get("timestamp")?,
        offer_version: row.get("offer_version")?,
        offer_type: row.get("offer_type")?,
        content: OfferContent {
            offer_throughput: row.get("offer_throughput")?,
        },
        resource: row.get("resource")?,
        offer_resource_id: row.get("offer_resource_id")?,
    })
}

// ─── JSON helpers ────────────────────────────────────────────────────────────

fn to_json<T: serde::Serialize>(value: &T) -> String {
    serde_json::to_string(value).unwrap_or_else(|_| "null".to_string())
}

fn from_json<T: serde::de::DeserializeOwned>(json: &str) -> CosmosResult<T> {
    serde_json::from_str(json)
        .map_err(|e| CosmosError::internal_server_error(format!("corrupt policy json: {e}")))
}

fn from_json_opt<T: serde::de::DeserializeOwned>(json: Option<String>) -> CosmosResult<Option<T>> {
    match json {
        Some(s) if !s.is_empty() => Ok(Some(from_json(&s)?)),
        _ => Ok(None),
    }
}

fn sqlite_err(e: rusqlite::Error) -> CosmosError {
    CosmosError::internal_server_error(format!("sqlite error: {e}"))
}

fn permission_mode_str(mode: PermissionMode) -> &'static str {
    match mode {
        PermissionMode::All => "All",
        PermissionMode::Read => "Read",
    }
}

fn insert_document(conn: &Connection, doc: &CosmosDocument) -> CosmosResult<()> {
    conn.execute(
        "INSERT OR REPLACE INTO documents \
         (database_id, container_id, partition_key_json, id, rid, etag, timestamp, body_json, ttl, lsn, is_indexed) \
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
        params![
            doc.database_id,
            doc.container_id,
            serialize_partition_key(&doc.partition_key),
            doc.id,
            doc.rid,
            doc.etag,
            doc.timestamp,
            serde_json::to_string(&doc.body).unwrap_or_else(|_| "{}".into()),
            doc.time_to_live,
            doc.lsn,
            doc.is_indexed as i64,
        ],
    )
    .map_err(sqlite_err)?;
    Ok(())
}

#[async_trait]
impl DocumentStore for SqliteDocumentStore {
    // ---------- Databases ----------

    async fn create_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        let conn = self.lock();
        let exists: bool = conn
            .query_row("SELECT 1 FROM databases WHERE id = ?1", params![id], |_| {
                Ok(())
            })
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if exists {
            return Err(CosmosError::conflict("database", id));
        }
        let db = CosmosDatabase::new(id);
        conn.execute(
            "INSERT INTO databases (id, rid, etag, timestamp, max_throughput) VALUES (?1, ?2, ?3, ?4, ?5)",
            params![db.id, db.rid, db.etag, db.timestamp, db.max_throughput],
        )
        .map_err(sqlite_err)?;
        Ok(db)
    }

    async fn get_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        let conn = self.lock();
        conn.query_row(
            "SELECT id, rid, etag, timestamp, max_throughput FROM databases WHERE id = ?1",
            params![id],
            row_to_database,
        )
        .optional()
        .map_err(sqlite_err)?
        .ok_or_else(|| CosmosError::not_found("database", id))
    }

    async fn list_databases(&self) -> CosmosResult<FeedResponse<CosmosDatabase>> {
        let conn = self.lock();
        let mut stmt = conn
            .prepare(
                "SELECT id, rid, etag, timestamp, max_throughput FROM databases ORDER BY id ASC",
            )
            .map_err(sqlite_err)?;
        let rows = stmt
            .query_map([], row_to_database)
            .map_err(sqlite_err)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(sqlite_err)?;
        Ok(FeedResponse::new(rows))
    }

    async fn replace_database(&self, database: CosmosDatabase) -> CosmosResult<CosmosDatabase> {
        let conn = self.lock();
        let existing = conn
            .query_row(
                "SELECT id, rid, etag, timestamp, max_throughput FROM databases WHERE id = ?1",
                params![database.id],
                row_to_database,
            )
            .optional()
            .map_err(sqlite_err)?
            .ok_or_else(|| CosmosError::not_found("database", &database.id))?;
        let updated = CosmosDatabase {
            id: database.id,
            rid: existing.rid,
            etag: etag(),
            timestamp: chrono::Utc::now().timestamp(),
            max_throughput: database.max_throughput,
        };
        conn.execute(
            "UPDATE databases SET rid = ?2, etag = ?3, timestamp = ?4, max_throughput = ?5 WHERE id = ?1",
            params![updated.id, updated.rid, updated.etag, updated.timestamp, updated.max_throughput],
        )
        .map_err(sqlite_err)?;
        Ok(updated)
    }

    async fn delete_database(&self, id: &str) -> CosmosResult<()> {
        let mut conn = self.lock();
        let exists: bool = conn
            .query_row("SELECT 1 FROM databases WHERE id = ?1", params![id], |_| {
                Ok(())
            })
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if !exists {
            return Err(CosmosError::not_found("database", id));
        }
        let tx = conn.transaction().map_err(sqlite_err)?;
        tx.execute("DELETE FROM documents WHERE database_id = ?1", params![id])
            .map_err(sqlite_err)?;
        tx.execute(
            "DELETE FROM offers WHERE offer_resource_id IN (SELECT rid FROM containers WHERE database_id = ?1)",
            params![id],
        )
        .map_err(sqlite_err)?;
        tx.execute("DELETE FROM containers WHERE database_id = ?1", params![id])
            .map_err(sqlite_err)?;
        tx.execute(
            "DELETE FROM permissions WHERE database_id = ?1",
            params![id],
        )
        .map_err(sqlite_err)?;
        tx.execute("DELETE FROM users WHERE database_id = ?1", params![id])
            .map_err(sqlite_err)?;
        tx.execute("DELETE FROM databases WHERE id = ?1", params![id])
            .map_err(sqlite_err)?;
        tx.commit().map_err(sqlite_err)?;
        Ok(())
    }

    // ---------- Containers ----------

    async fn create_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        let conn = self.lock();
        let db_exists: bool = conn
            .query_row(
                "SELECT 1 FROM databases WHERE id = ?1",
                params![database_id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if !db_exists {
            return Err(CosmosError::not_found("database", database_id));
        }
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM containers WHERE database_id = ?1 AND id = ?2",
                params![database_id, container.id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if exists {
            return Err(CosmosError::conflict("container", &container.id));
        }
        let mut container = container;
        container.database_id = database_id.to_string();
        conn.execute(
            "INSERT INTO containers \
             (database_id, id, rid, etag, timestamp, partition_key_json, indexing_policy_json, \
              unique_key_policy_json, conflict_resolution_policy_json, vector_embedding_policy_json, \
              default_ttl, max_throughput) \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
            params![
                container.database_id,
                container.id,
                container.rid,
                container.etag,
                container.timestamp,
                to_json(&container.partition_key),
                to_json(&container.indexing_policy),
                container.unique_key_policy.as_ref().map(to_json),
                container.conflict_resolution_policy.as_ref().map(to_json),
                container.vector_embedding_policy.as_ref().map(to_json),
                container.default_time_to_live,
                container.max_throughput,
            ],
        )
        .map_err(sqlite_err)?;
        Ok(container)
    }

    async fn get_container(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<CosmosContainer> {
        let conn = self.lock();
        let row = conn
            .query_row(
                "SELECT * FROM containers WHERE database_id = ?1 AND id = ?2",
                params![database_id, container_id],
                |r| Ok(row_to_container(r)),
            )
            .optional()
            .map_err(sqlite_err)?;
        match row {
            Some(res) => res,
            None => Err(CosmosError::not_found("container", container_id)),
        }
    }

    async fn list_containers(
        &self,
        database_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosContainer>> {
        let conn = self.lock();
        let mut stmt = conn
            .prepare("SELECT * FROM containers WHERE database_id = ?1 ORDER BY id ASC")
            .map_err(sqlite_err)?;
        let rows = stmt
            .query_map(params![database_id], |r| Ok(row_to_container(r)))
            .map_err(sqlite_err)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(sqlite_err)?
            .into_iter()
            .collect::<CosmosResult<Vec<_>>>()?;
        Ok(FeedResponse::new(rows))
    }

    async fn replace_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        let conn = self.lock();
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM containers WHERE database_id = ?1 AND id = ?2",
                params![database_id, container.id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if !exists {
            return Err(CosmosError::not_found("container", &container.id));
        }
        let mut container = container;
        container.database_id = database_id.to_string();
        conn.execute(
            "UPDATE containers SET rid = ?3, etag = ?4, timestamp = ?5, partition_key_json = ?6, \
             indexing_policy_json = ?7, unique_key_policy_json = ?8, conflict_resolution_policy_json = ?9, \
             vector_embedding_policy_json = ?10, default_ttl = ?11, max_throughput = ?12 \
             WHERE database_id = ?1 AND id = ?2",
            params![
                container.database_id,
                container.id,
                container.rid,
                container.etag,
                container.timestamp,
                to_json(&container.partition_key),
                to_json(&container.indexing_policy),
                container.unique_key_policy.as_ref().map(to_json),
                container.conflict_resolution_policy.as_ref().map(to_json),
                container.vector_embedding_policy.as_ref().map(to_json),
                container.default_time_to_live,
                container.max_throughput,
            ],
        )
        .map_err(sqlite_err)?;
        Ok(container)
    }

    async fn delete_container(&self, database_id: &str, container_id: &str) -> CosmosResult<()> {
        let mut conn = self.lock();
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM containers WHERE database_id = ?1 AND id = ?2",
                params![database_id, container_id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if !exists {
            return Err(CosmosError::not_found("container", container_id));
        }
        let tx = conn.transaction().map_err(sqlite_err)?;
        tx.execute(
            "DELETE FROM documents WHERE database_id = ?1 AND container_id = ?2",
            params![database_id, container_id],
        )
        .map_err(sqlite_err)?;
        tx.execute(
            "DELETE FROM containers WHERE database_id = ?1 AND id = ?2",
            params![database_id, container_id],
        )
        .map_err(sqlite_err)?;
        tx.commit().map_err(sqlite_err)?;
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
        let conn = self.lock();
        let container = conn
            .query_row(
                "SELECT * FROM containers WHERE database_id = ?1 AND id = ?2",
                params![database_id, container_id],
                |r| Ok(row_to_container(r)),
            )
            .optional()
            .map_err(sqlite_err)?
            .ok_or_else(|| CosmosError::not_found("container", container_id))??;
        let id = require_id(&document)?;
        let pk = extract_partition_key(&container, &document);
        let pk_json = serialize_partition_key(&pk);
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM documents WHERE database_id = ?1 AND container_id = ?2 AND partition_key_json = ?3 AND id = ?4",
                params![database_id, container_id, pk_json, id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if exists {
            return Err(CosmosError::conflict("document", &id));
        }
        let mut doc = CosmosDocument::new(database_id, container_id, id, pk, document);
        doc.lsn = allocate_next_lsn(&conn)?;
        doc.is_indexed = is_indexed.unwrap_or(true);
        insert_document(&conn, &doc)?;
        record_change(&conn, &doc, ChangeType::Create)?;
        Ok(doc)
    }

    async fn read_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<CosmosDocument> {
        let conn = self.lock();
        let pk_json = serialize_partition_key(partition_key);
        conn.query_row(
            "SELECT * FROM documents WHERE database_id = ?1 AND container_id = ?2 AND partition_key_json = ?3 AND id = ?4",
            params![database_id, container_id, pk_json, document_id],
            |r| Ok(row_to_document(r)),
        )
        .optional()
        .map_err(sqlite_err)?
        .transpose()?
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
        let conn = self.lock();
        let container = conn
            .query_row(
                "SELECT * FROM containers WHERE database_id = ?1 AND id = ?2",
                params![database_id, container_id],
                |r| Ok(row_to_container(r)),
            )
            .optional()
            .map_err(sqlite_err)?
            .ok_or_else(|| CosmosError::not_found("container", container_id))??;
        let pk = extract_partition_key(&container, &document);
        let pk_json = serialize_partition_key(&pk);
        let existing = conn
            .query_row(
                "SELECT * FROM documents WHERE database_id = ?1 AND container_id = ?2 AND partition_key_json = ?3 AND id = ?4",
                params![database_id, container_id, pk_json, document_id],
                |r| Ok(row_to_document(r)),
            )
            .optional()
            .map_err(sqlite_err)?
            .transpose()?
            .ok_or_else(|| CosmosError::not_found("document", document_id))?;
        if let Some(expected) = if_match {
            if existing.etag != expected {
                return Err(CosmosError::precondition_failed(
                    "ETag does not match for replace.",
                ));
            }
        }
        let mut doc = CosmosDocument::new(database_id, container_id, document_id, pk, document);
        doc.rid = existing.rid;
        doc.lsn = allocate_next_lsn(&conn)?;
        doc.etag = etag();
        doc.is_indexed = is_indexed.unwrap_or(true);
        insert_document(&conn, &doc)?;
        record_change(&conn, &doc, ChangeType::Replace)?;
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
        let conn = self.lock();
        let pk_json = serialize_partition_key(partition_key);
        let existing = conn
            .query_row(
                "SELECT * FROM documents WHERE database_id = ?1 AND container_id = ?2 AND partition_key_json = ?3 AND id = ?4",
                params![database_id, container_id, pk_json, document_id],
                |r| Ok(row_to_document(r)),
            )
            .optional()
            .map_err(sqlite_err)?
            .transpose()?
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
        doc.lsn = allocate_next_lsn(&conn)?;
        doc.etag = etag();
        insert_document(&conn, &doc)?;
        record_change(&conn, &doc, ChangeType::Replace)?;
        Ok(doc)
    }

    async fn delete_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<()> {
        let conn = self.lock();
        let pk_json = serialize_partition_key(partition_key);
        let mut existing = conn
            .query_row(
                "SELECT * FROM documents WHERE database_id = ?1 AND container_id = ?2 AND partition_key_json = ?3 AND id = ?4",
                params![database_id, container_id, pk_json, document_id],
                |r| Ok(row_to_document(r)),
            )
            .optional()
            .map_err(sqlite_err)?
            .transpose()?
            .ok_or_else(|| CosmosError::not_found("document", document_id))?;
        conn.execute(
            "DELETE FROM documents WHERE database_id = ?1 AND container_id = ?2 AND partition_key_json = ?3 AND id = ?4",
            params![database_id, container_id, pk_json, document_id],
        )
        .map_err(sqlite_err)?;
        existing.lsn = allocate_next_lsn(&conn)?;
        record_change(&conn, &existing, ChangeType::Delete)?;
        Ok(())
    }

    async fn empty_container(&self, database_id: &str, container_id: &str) -> CosmosResult<usize> {
        let conn = self.lock();
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM containers WHERE database_id = ?1 AND id = ?2",
                params![database_id, container_id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if !exists {
            return Err(CosmosError::not_found("container", container_id));
        }
        let count: i64 = conn
            .query_row(
                "SELECT COUNT(*) FROM documents WHERE database_id = ?1 AND container_id = ?2",
                params![database_id, container_id],
                |r| r.get(0),
            )
            .map_err(sqlite_err)?;
        conn.execute(
            "DELETE FROM documents WHERE database_id = ?1 AND container_id = ?2",
            params![database_id, container_id],
        )
        .map_err(sqlite_err)?;
        Ok(count as usize)
    }

    async fn get_global_lsn(&self) -> CosmosResult<i64> {
        let conn = self.lock();
        read_global_lsn(&conn)
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
        let conn = self.lock();
        let mut stmt = conn
            .prepare(
                "SELECT * FROM documents WHERE database_id = ?1 AND container_id = ?2 ORDER BY id ASC",
            )
            .map_err(sqlite_err)?;
        let rows = stmt
            .query_map(params![database_id, container_id], |r| {
                Ok(row_to_document(r))
            })
            .map_err(sqlite_err)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(sqlite_err)?
            .into_iter()
            .collect::<CosmosResult<Vec<_>>>()?;
        Ok(FeedResponse::new(rows))
    }

    // ---------- Users ----------

    async fn create_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        let conn = self.lock();
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM users WHERE database_id = ?1 AND id = ?2",
                params![database_id, user_id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if exists {
            return Err(CosmosError::conflict("user", user_id));
        }
        let user = CosmosUser::new(database_id, user_id);
        conn.execute(
            "INSERT INTO users (database_id, id, rid, etag, timestamp) VALUES (?1, ?2, ?3, ?4, ?5)",
            params![
                user.database_id,
                user.id,
                user.rid,
                user.etag,
                user.timestamp
            ],
        )
        .map_err(sqlite_err)?;
        Ok(user)
    }

    async fn get_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        let conn = self.lock();
        conn.query_row(
            "SELECT database_id, id, rid, etag, timestamp FROM users WHERE database_id = ?1 AND id = ?2",
            params![database_id, user_id],
            row_to_user,
        )
        .optional()
        .map_err(sqlite_err)?
        .ok_or_else(|| CosmosError::not_found("user", user_id))
    }

    async fn list_users(&self, database_id: &str) -> CosmosResult<FeedResponse<CosmosUser>> {
        let conn = self.lock();
        let mut stmt = conn
            .prepare("SELECT database_id, id, rid, etag, timestamp FROM users WHERE database_id = ?1 ORDER BY id ASC")
            .map_err(sqlite_err)?;
        let rows = stmt
            .query_map(params![database_id], row_to_user)
            .map_err(sqlite_err)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(sqlite_err)?;
        Ok(FeedResponse::new(rows))
    }

    async fn replace_user(&self, database_id: &str, user: CosmosUser) -> CosmosResult<CosmosUser> {
        let conn = self.lock();
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM users WHERE database_id = ?1 AND id = ?2",
                params![database_id, user.id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if !exists {
            return Err(CosmosError::not_found("user", &user.id));
        }
        let mut user = user;
        user.database_id = database_id.to_string();
        conn.execute(
            "UPDATE users SET rid = ?3, etag = ?4, timestamp = ?5 WHERE database_id = ?1 AND id = ?2",
            params![user.database_id, user.id, user.rid, user.etag, user.timestamp],
        )
        .map_err(sqlite_err)?;
        Ok(user)
    }

    async fn delete_user(&self, database_id: &str, user_id: &str) -> CosmosResult<()> {
        let conn = self.lock();
        let affected = conn
            .execute(
                "DELETE FROM users WHERE database_id = ?1 AND id = ?2",
                params![database_id, user_id],
            )
            .map_err(sqlite_err)?;
        if affected == 0 {
            return Err(CosmosError::not_found("user", user_id));
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
        let conn = self.lock();
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM permissions WHERE database_id = ?1 AND user_id = ?2 AND id = ?3",
                params![database_id, user_id, permission.id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if exists {
            return Err(CosmosError::conflict("permission", &permission.id));
        }
        conn.execute(
            "INSERT INTO permissions (database_id, user_id, id, rid, etag, timestamp, permission_mode, resource, token) \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                database_id,
                user_id,
                permission.id,
                permission.rid,
                permission.etag,
                permission.timestamp,
                permission_mode_str(permission.permission_mode),
                permission.resource,
                permission.token,
            ],
        )
        .map_err(sqlite_err)?;
        Ok(permission)
    }

    async fn get_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<CosmosPermission> {
        let conn = self.lock();
        conn.query_row(
            "SELECT * FROM permissions WHERE database_id = ?1 AND user_id = ?2 AND id = ?3",
            params![database_id, user_id, permission_id],
            |r| Ok(row_to_permission(r)),
        )
        .optional()
        .map_err(sqlite_err)?
        .transpose()?
        .ok_or_else(|| CosmosError::not_found("permission", permission_id))
    }

    async fn list_permissions(
        &self,
        database_id: &str,
        user_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosPermission>> {
        let conn = self.lock();
        let mut stmt = conn
            .prepare(
                "SELECT * FROM permissions WHERE database_id = ?1 AND user_id = ?2 ORDER BY id ASC",
            )
            .map_err(sqlite_err)?;
        let rows = stmt
            .query_map(params![database_id, user_id], |r| Ok(row_to_permission(r)))
            .map_err(sqlite_err)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(sqlite_err)?
            .into_iter()
            .collect::<CosmosResult<Vec<_>>>()?;
        Ok(FeedResponse::new(rows))
    }

    async fn replace_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        let conn = self.lock();
        let exists: bool = conn
            .query_row(
                "SELECT 1 FROM permissions WHERE database_id = ?1 AND user_id = ?2 AND id = ?3",
                params![database_id, user_id, permission.id],
                |_| Ok(()),
            )
            .optional()
            .map_err(sqlite_err)?
            .is_some();
        if !exists {
            return Err(CosmosError::not_found("permission", &permission.id));
        }
        conn.execute(
            "UPDATE permissions SET rid = ?4, etag = ?5, timestamp = ?6, permission_mode = ?7, resource = ?8, token = ?9 \
             WHERE database_id = ?1 AND user_id = ?2 AND id = ?3",
            params![
                database_id,
                user_id,
                permission.id,
                permission.rid,
                permission.etag,
                permission.timestamp,
                permission_mode_str(permission.permission_mode),
                permission.resource,
                permission.token,
            ],
        )
        .map_err(sqlite_err)?;
        Ok(permission)
    }

    async fn delete_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<()> {
        let conn = self.lock();
        let affected = conn
            .execute(
                "DELETE FROM permissions WHERE database_id = ?1 AND user_id = ?2 AND id = ?3",
                params![database_id, user_id, permission_id],
            )
            .map_err(sqlite_err)?;
        if affected == 0 {
            return Err(CosmosError::not_found("permission", permission_id));
        }
        Ok(())
    }

    // ---------- Offers ----------

    async fn get_offer(&self, offer_id: &str) -> CosmosResult<CosmosOffer> {
        let conn = self.lock();
        conn.query_row(
            "SELECT * FROM offers WHERE id = ?1",
            params![offer_id],
            row_to_offer,
        )
        .optional()
        .map_err(sqlite_err)?
        .ok_or_else(|| CosmosError::not_found("offer", offer_id))
    }

    async fn list_offers(&self) -> CosmosResult<FeedResponse<CosmosOffer>> {
        let conn = self.lock();
        let mut stmt = conn
            .prepare("SELECT * FROM offers ORDER BY id ASC")
            .map_err(sqlite_err)?;
        let rows = stmt
            .query_map([], row_to_offer)
            .map_err(sqlite_err)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(sqlite_err)?;
        Ok(FeedResponse::new(rows))
    }

    async fn replace_offer(&self, offer: CosmosOffer) -> CosmosResult<CosmosOffer> {
        let conn = self.lock();
        conn.execute(
            "INSERT OR REPLACE INTO offers \
             (id, rid, etag, timestamp, offer_version, offer_type, offer_throughput, resource, offer_resource_id) \
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            params![
                offer.id,
                offer.rid,
                offer.etag,
                offer.timestamp,
                offer.offer_version,
                offer.offer_type,
                offer.content.offer_throughput,
                offer.resource,
                offer.offer_resource_id,
            ],
        )
        .map_err(sqlite_err)?;
        Ok(offer)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use cosmos_core::ids::resource_id;
    use cosmos_core::models::PartitionKeyDefinition;
    use serde_json::json;

    fn body(id: &str, pk: &str) -> JsonObject {
        json!({ "id": id, "pk": pk, "value": 1 })
            .as_object()
            .unwrap()
            .clone()
    }

    async fn seed(store: &SqliteDocumentStore) -> CosmosContainer {
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
        let store = SqliteDocumentStore::in_memory().unwrap();
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
        let store = SqliteDocumentStore::in_memory().unwrap();
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

        // Reading with the wrong partition key is a 404.
        let wrong_pk = PartitionKeyValue::single(json!("tenant-b"));
        let err = store
            .read_document("db1", "coll1", "doc1", &wrong_pk)
            .await
            .unwrap_err();
        assert_eq!(err.status_code, 404);
    }

    #[tokio::test]
    async fn upsert_patch_and_lsn_progression() {
        let store = SqliteDocumentStore::in_memory().unwrap();
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
        let dir = std::env::temp_dir().join(format!("cosmos-sqlite-test-{}", uuid_like()));
        {
            let store = SqliteDocumentStore::open(&dir).unwrap();
            seed(&store).await;
            store
                .create_document("db1", "coll1", body("doc1", "tenant-a"), None)
                .await
                .unwrap();
        }
        {
            let store = SqliteDocumentStore::open(&dir).unwrap();
            let pk = PartitionKeyValue::single(json!("tenant-a"));
            let read = store
                .read_document("db1", "coll1", "doc1", &pk)
                .await
                .unwrap();
            assert_eq!(read.id, "doc1");
        }
        let _ = std::fs::remove_dir_all(&dir);
    }

    fn uuid_like() -> String {
        format!("{}-{}", std::process::id(), resource_id())
    }
}
