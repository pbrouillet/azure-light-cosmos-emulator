//! Change-feed providers. Ports `InMemoryChangeFeedProvider` and
//! `SqliteChangeFeedProvider` from `src/Storage/ChangeFeed/*` and
//! `src/Storage/Sqlite/SqliteChangeFeedProvider.cs`.
//!
//! The change feed records every document mutation (create/replace/delete) and
//! lets consumers read forward from an LSN cursor. The InMemory provider keeps
//! an in-memory log (shared with [`crate::InMemoryDocumentStore`] via an
//! `Arc<InMemoryChangeLog>`); the SQLite provider shares the store's connection
//! and reads the `changefeed` table the store writes to.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use cosmos_core::error::{CosmosError, CosmosResult};
use cosmos_core::models::*;
use cosmos_core::traits::{ChangeFeedOptions, ChangeFeedProvider};
use rusqlite::{params, Connection, OptionalExtension};

use crate::common::{deserialize_partition_key, serialize_partition_key};
use crate::sqlite::{change_type_from_code, insert_change_row};

/// Hard cap on retained changes per container (matches the .NET default) to
/// bound memory for the in-memory backend; oldest entries are evicted first.
const MAX_ITEMS_PER_CONTAINER: usize = 100_000;

fn feed_key(database_id: &str, container_id: &str) -> String {
    format!("{database_id}/{container_id}")
}

/// Applies the shared post-fetch filtering (`start_lsn`, partition key,
/// full-fidelity, max item count) over an ordered slice of change items,
/// returning the page and the last LSN to use as the next continuation token.
pub(crate) fn page_items(
    items: &[ChangeFeedItem],
    start_lsn: i64,
    options: &ChangeFeedOptions,
) -> (Vec<ChangeFeedItem>, i64) {
    let max_items = options.max_item_count.unwrap_or(100).max(0) as usize;
    let mut result = Vec::new();
    let mut last_lsn = start_lsn;
    for item in items.iter().filter(|i| i.lsn > start_lsn) {
        if let Some(pk) = &options.partition_key {
            if &item.document.partition_key != pk {
                continue;
            }
        }
        if !options.full_fidelity && item.change_type == ChangeType::Delete {
            continue;
        }
        result.push(item.clone());
        last_lsn = item.lsn;
        if result.len() >= max_items {
            break;
        }
    }
    (result, last_lsn)
}

// ─── In-memory ───────────────────────────────────────────────────────────────

/// Shared, append-only in-memory change log keyed by `{db}/{coll}`.
#[derive(Default)]
pub struct InMemoryChangeLog {
    inner: Mutex<HashMap<String, Vec<ChangeFeedItem>>>,
}

impl InMemoryChangeLog {
    /// Records a change synchronously (no `await`), so document stores can call
    /// it while holding their own state lock without risking a deadlock.
    pub fn record(
        &self,
        database_id: &str,
        container_id: &str,
        document: CosmosDocument,
        change_type: ChangeType,
        previous_image: Option<CosmosDocument>,
    ) {
        let lsn = document.lsn;
        let mut item = ChangeFeedItem::new(document, lsn, change_type);
        item.previous_image = previous_image;
        let mut map = self.inner.lock().expect("change log poisoned");
        let items = map.entry(feed_key(database_id, container_id)).or_default();
        items.push(item);
        if items.len() > MAX_ITEMS_PER_CONTAINER {
            let overflow = items.len() - MAX_ITEMS_PER_CONTAINER;
            items.drain(0..overflow);
        }
    }

    fn read(
        &self,
        database_id: &str,
        container_id: &str,
        options: &ChangeFeedOptions,
    ) -> FeedResponse<ChangeFeedItem> {
        let map = self.inner.lock().expect("change log poisoned");
        let items = match map.get(&feed_key(database_id, container_id)) {
            Some(items) => items,
            None => {
                let mut feed = FeedResponse::new(Vec::new());
                feed.continuation_token = Some("0".to_string());
                return feed;
            }
        };

        let start_lsn = resolve_start_lsn_in_memory(items, options);
        let (result, last_lsn) = page_items(items, start_lsn, options);
        let mut feed = FeedResponse::new(result);
        feed.continuation_token = Some(last_lsn.to_string());
        feed
    }

    fn trim(&self, retention: Duration) {
        let cutoff = Utc::now() - chrono::Duration::from_std(retention).unwrap_or_default();
        let mut map = self.inner.lock().expect("change log poisoned");
        map.retain(|_, items| {
            items.retain(|i| i.timestamp >= cutoff);
            !items.is_empty()
        });
    }
}

fn resolve_start_lsn_in_memory(items: &[ChangeFeedItem], options: &ChangeFeedOptions) -> i64 {
    if let Some(token) = &options.continuation_token {
        if let Ok(lsn) = token.parse::<i64>() {
            return lsn;
        }
    }
    if let Some(start_time) = options.start_time {
        return items
            .iter()
            .filter(|i| i.timestamp >= start_time)
            .map(|i| i.lsn)
            .min()
            .unwrap_or(0);
    }
    if !options.start_from_beginning {
        return items.last().map(|i| i.lsn).unwrap_or(0);
    }
    0
}

/// In-memory change-feed provider reading a shared [`InMemoryChangeLog`].
pub struct InMemoryChangeFeedProvider {
    log: Arc<InMemoryChangeLog>,
}

impl InMemoryChangeFeedProvider {
    pub fn new(log: Arc<InMemoryChangeLog>) -> Self {
        Self { log }
    }
}

#[async_trait]
impl ChangeFeedProvider for InMemoryChangeFeedProvider {
    async fn read_change_feed(
        &self,
        database_id: &str,
        container_id: &str,
        options: ChangeFeedOptions,
    ) -> CosmosResult<FeedResponse<ChangeFeedItem>> {
        Ok(self.log.read(database_id, container_id, &options))
    }

    async fn record_change(
        &self,
        database_id: &str,
        container_id: &str,
        document: &CosmosDocument,
        change_type: ChangeType,
        previous_image: Option<&CosmosDocument>,
    ) -> CosmosResult<()> {
        self.log.record(
            database_id,
            container_id,
            document.clone(),
            change_type,
            previous_image.cloned(),
        );
        Ok(())
    }

    async fn trim(&self, retention: Duration) -> CosmosResult<()> {
        self.log.trim(retention);
        Ok(())
    }
}

// ─── SQLite ──────────────────────────────────────────────────────────────────

/// SQLite change-feed provider sharing the document store's connection.
pub struct SqliteChangeFeedProvider {
    conn: Arc<Mutex<Connection>>,
}

impl SqliteChangeFeedProvider {
    pub fn new(conn: Arc<Mutex<Connection>>) -> Self {
        Self { conn }
    }

    fn resolve_start_lsn(
        conn: &Connection,
        database_id: &str,
        container_id: &str,
        options: &ChangeFeedOptions,
    ) -> CosmosResult<i64> {
        if let Some(token) = &options.continuation_token {
            if let Ok(lsn) = token.parse::<i64>() {
                return Ok(lsn);
            }
        }
        if let Some(start_time) = options.start_time {
            let min: Option<i64> = conn
                .query_row(
                    "SELECT MIN(lsn) FROM changefeed WHERE database_id = ?1 AND container_id = ?2 AND timestamp >= ?3",
                    params![database_id, container_id, start_time.to_rfc3339()],
                    |r| r.get(0),
                )
                .optional()
                .map_err(map_err)?
                .flatten();
            return Ok(match min {
                Some(v) if v > 0 => v - 1,
                _ => 0,
            });
        }
        if !options.start_from_beginning {
            let max: i64 = conn
                .query_row(
                    "SELECT COALESCE(MAX(lsn), 0) FROM changefeed WHERE database_id = ?1 AND container_id = ?2",
                    params![database_id, container_id],
                    |r| r.get(0),
                )
                .map_err(map_err)?;
            return Ok(max);
        }
        Ok(0)
    }
}

fn map_err(e: rusqlite::Error) -> CosmosError {
    CosmosError::internal_server_error(format!("sqlite error: {e}"))
}

fn parse_previous_image(
    json: Option<String>,
    database_id: &str,
    container_id: &str,
    pk: &PartitionKeyValue,
    fallback_id: &str,
) -> Option<CosmosDocument> {
    let json = json?;
    let value: serde_json::Value = serde_json::from_str(&json).ok()?;
    let id = value
        .get("id")
        .and_then(|v| v.as_str())
        .unwrap_or(fallback_id)
        .to_string();
    let body = value
        .get("bodyJson")
        .and_then(|v| v.as_str())
        .and_then(|s| serde_json::from_str::<JsonObject>(s).ok())
        .unwrap_or_default();
    let lsn = value.get("lsn").and_then(|v| v.as_i64()).unwrap_or(0);
    let mut doc = CosmosDocument::new(database_id, container_id, id, pk.clone(), body);
    doc.lsn = lsn;
    Some(doc)
}

#[async_trait]
impl ChangeFeedProvider for SqliteChangeFeedProvider {
    async fn read_change_feed(
        &self,
        database_id: &str,
        container_id: &str,
        options: ChangeFeedOptions,
    ) -> CosmosResult<FeedResponse<ChangeFeedItem>> {
        let conn = self.conn.lock().expect("sqlite mutex poisoned");
        let start_lsn = Self::resolve_start_lsn(&conn, database_id, container_id, &options)?;
        let max_items = options.max_item_count.unwrap_or(100).max(0) as i64;

        let mut sql = String::from(
            "SELECT document_id, lsn, change_type, body_json, previous_image_json, partition_key_json, timestamp \
             FROM changefeed WHERE database_id = ?1 AND container_id = ?2 AND lsn > ?3",
        );
        let pk_json = options.partition_key.as_ref().map(serialize_partition_key);
        if pk_json.is_some() {
            sql.push_str(" AND partition_key_json = ?4");
        }
        if !options.full_fidelity {
            sql.push_str(" AND change_type != 2");
        }
        sql.push_str(" ORDER BY lsn ASC LIMIT ");
        sql.push_str(&max_items.to_string());

        let mut stmt = conn.prepare(&sql).map_err(map_err)?;

        let map_row = |row: &rusqlite::Row| -> rusqlite::Result<ChangeFeedItem> {
            let doc_id: String = row.get(0)?;
            let lsn: i64 = row.get(1)?;
            let change_type = change_type_from_code(row.get::<_, i64>(2)?);
            let body_json: String = row.get(3)?;
            let previous_json: Option<String> = row.get(4)?;
            let pk_json: String = row.get(5)?;
            let ts: String = row.get(6)?;

            let pk = deserialize_partition_key(&pk_json);
            let body: JsonObject = serde_json::from_str(&body_json).unwrap_or_default();
            let mut document =
                CosmosDocument::new(database_id, container_id, doc_id.clone(), pk.clone(), body);
            document.lsn = lsn;
            let previous_image =
                parse_previous_image(previous_json, database_id, container_id, &pk, &doc_id);
            let mut item = ChangeFeedItem::new(document, lsn, change_type);
            item.previous_image = previous_image;
            item.timestamp = DateTime::parse_from_rfc3339(&ts)
                .map(|t| t.with_timezone(&Utc))
                .unwrap_or_else(|_| Utc::now());
            Ok(item)
        };

        let rows = {
            let mut args: Vec<rusqlite::types::Value> = vec![
                rusqlite::types::Value::Text(database_id.to_string()),
                rusqlite::types::Value::Text(container_id.to_string()),
                rusqlite::types::Value::Integer(start_lsn),
            ];
            if let Some(pk) = &pk_json {
                args.push(rusqlite::types::Value::Text(pk.clone()));
            }
            stmt.query_map(rusqlite::params_from_iter(args.iter()), map_row)
                .map_err(map_err)?
                .collect::<rusqlite::Result<Vec<_>>>()
                .map_err(map_err)?
        };

        let last_lsn = rows.last().map(|i| i.lsn).unwrap_or(start_lsn);
        let mut feed = FeedResponse::new(rows);
        feed.continuation_token = Some(last_lsn.to_string());
        Ok(feed)
    }

    async fn record_change(
        &self,
        _database_id: &str,
        _container_id: &str,
        document: &CosmosDocument,
        change_type: ChangeType,
        previous_image: Option<&CosmosDocument>,
    ) -> CosmosResult<()> {
        let conn = self.conn.lock().expect("sqlite mutex poisoned");
        insert_change_row(&conn, document, change_type, previous_image)
    }

    async fn trim(&self, retention: Duration) -> CosmosResult<()> {
        let cutoff =
            (Utc::now() - chrono::Duration::from_std(retention).unwrap_or_default()).to_rfc3339();
        let conn = self.conn.lock().expect("sqlite mutex poisoned");
        conn.execute(
            "DELETE FROM changefeed WHERE timestamp < ?1",
            params![cutoff],
        )
        .map_err(map_err)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{InMemoryDocumentStore, SqliteDocumentStore};
    use cosmos_core::models::PartitionKeyDefinition;
    use cosmos_core::traits::DocumentStore;
    use serde_json::json;

    fn body(id: &str, pk: &str, value: i64) -> JsonObject {
        json!({ "id": id, "pk": pk, "value": value })
            .as_object()
            .unwrap()
            .clone()
    }

    fn from_beginning() -> ChangeFeedOptions {
        ChangeFeedOptions {
            start_from_beginning: true,
            ..Default::default()
        }
    }

    async fn seed<S: DocumentStore>(store: &S) {
        store.create_database("db1").await.unwrap();
        let container = CosmosContainer::new(
            "db1",
            "coll1",
            PartitionKeyDefinition::new(vec!["/pk".to_string()]),
        );
        store.create_container("db1", container).await.unwrap();
    }

    async fn assert_change_feed_semantics<S, P>(store: S, provider: P)
    where
        S: DocumentStore,
        P: ChangeFeedProvider,
    {
        seed(&store).await;
        store
            .create_document("db1", "coll1", body("d1", "a", 1), None)
            .await
            .unwrap();
        store
            .create_document("db1", "coll1", body("d2", "b", 1), None)
            .await
            .unwrap();

        // Read from the beginning: both creates are visible, token advances.
        let feed = provider
            .read_change_feed("db1", "coll1", from_beginning())
            .await
            .unwrap();
        assert_eq!(feed.resources.len(), 2);
        let token = feed.continuation_token.clone().unwrap();

        // Reading again from the token yields nothing new.
        let opts = ChangeFeedOptions {
            continuation_token: Some(token.clone()),
            ..Default::default()
        };
        let empty = provider
            .read_change_feed("db1", "coll1", opts)
            .await
            .unwrap();
        assert_eq!(empty.resources.len(), 0);

        // A replace shows up after the token, as a Replace with a previous image.
        store
            .replace_document("db1", "coll1", "d1", body("d1", "a", 2), None, None)
            .await
            .unwrap();
        let opts = ChangeFeedOptions {
            continuation_token: Some(token.clone()),
            ..Default::default()
        };
        let after = provider
            .read_change_feed("db1", "coll1", opts)
            .await
            .unwrap();
        assert_eq!(after.resources.len(), 1);
        assert_eq!(after.resources[0].change_type, ChangeType::Replace);
        assert!(after.resources[0].previous_image.is_some());

        // Partition-key filter restricts to matching documents.
        let opts = ChangeFeedOptions {
            start_from_beginning: true,
            partition_key: Some(PartitionKeyValue::single(json!("b"))),
            ..Default::default()
        };
        let only_b = provider
            .read_change_feed("db1", "coll1", opts)
            .await
            .unwrap();
        assert_eq!(only_b.resources.len(), 1);
        assert_eq!(only_b.resources[0].document.id, "d2");

        // Deletes are hidden by default but visible in full-fidelity mode.
        store
            .delete_document("db1", "coll1", "d2", &PartitionKeyValue::single(json!("b")))
            .await
            .unwrap();
        let default_feed = provider
            .read_change_feed("db1", "coll1", from_beginning())
            .await
            .unwrap();
        assert!(default_feed
            .resources
            .iter()
            .all(|i| i.change_type != ChangeType::Delete));
        let full = ChangeFeedOptions {
            start_from_beginning: true,
            full_fidelity: true,
            ..Default::default()
        };
        let full_feed = provider
            .read_change_feed("db1", "coll1", full)
            .await
            .unwrap();
        assert!(full_feed
            .resources
            .iter()
            .any(|i| i.change_type == ChangeType::Delete));
    }

    #[tokio::test]
    async fn in_memory_change_feed() {
        let store = InMemoryDocumentStore::new();
        let provider = store.change_feed();
        assert_change_feed_semantics(store, provider).await;
    }

    #[tokio::test]
    async fn sqlite_change_feed() {
        let store = SqliteDocumentStore::in_memory().unwrap();
        let provider = store.change_feed();
        assert_change_feed_semantics(store, provider).await;
    }
}
