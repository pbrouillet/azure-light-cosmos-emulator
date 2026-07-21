//! In-memory `DocumentStore` backend. Ports `InMemoryDocumentStore`.
//!
//! Uses composite keys mirroring the .NET layout:
//! databases keyed by `id`, containers by `{db}/{coll}`,
//! documents by `{db}/{coll}` → map of `{pk}/{docId}` → document.

use std::collections::HashMap;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use cosmos_core::error::{CosmosError, CosmosResult};
use cosmos_core::ids::etag;
use cosmos_core::models::*;
use cosmos_core::traits::DocumentStore;

use crate::changefeed::{InMemoryChangeFeedProvider, InMemoryChangeLog};
use crate::common::{apply_patch, extract_partition_key, require_id};

#[derive(Default)]
struct State {
    databases: HashMap<String, CosmosDatabase>,
    containers: HashMap<String, CosmosContainer>,
    documents: HashMap<String, HashMap<String, CosmosDocument>>,
    users: HashMap<String, CosmosUser>,
    permissions: HashMap<String, CosmosPermission>,
    offers: HashMap<String, CosmosOffer>,
}

/// In-memory (ephemeral) document store backed by hash maps.
pub struct InMemoryDocumentStore {
    state: Mutex<State>,
    global_lsn: AtomicI64,
    change_log: Arc<InMemoryChangeLog>,
}

impl Default for InMemoryDocumentStore {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemoryDocumentStore {
    pub fn new() -> Self {
        Self {
            state: Mutex::new(State::default()),
            global_lsn: AtomicI64::new(0),
            change_log: Arc::new(InMemoryChangeLog::default()),
        }
    }

    fn coll_key(database_id: &str, container_id: &str) -> String {
        format!("{database_id}/{container_id}")
    }

    fn doc_key(partition_key: &PartitionKeyValue, document_id: &str) -> String {
        format!("{}/{document_id}", partition_key.to_header_string())
    }

    fn next_lsn(&self) -> i64 {
        self.global_lsn.fetch_add(1, Ordering::SeqCst) + 1
    }

    /// Returns a change-feed provider sharing this store's in-memory change log.
    pub fn change_feed(&self) -> InMemoryChangeFeedProvider {
        InMemoryChangeFeedProvider::new(Arc::clone(&self.change_log))
    }
}

#[async_trait]
impl DocumentStore for InMemoryDocumentStore {
    // ---------- Databases ----------

    async fn create_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        let mut state = self.state.lock().unwrap();
        if state.databases.contains_key(id) {
            return Err(CosmosError::conflict("database", id));
        }
        let db = CosmosDatabase::new(id);
        state.databases.insert(id.to_string(), db.clone());
        Ok(db)
    }

    async fn get_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        let state = self.state.lock().unwrap();
        state
            .databases
            .get(id)
            .cloned()
            .ok_or_else(|| CosmosError::not_found("database", id))
    }

    async fn list_databases(&self) -> CosmosResult<FeedResponse<CosmosDatabase>> {
        let state = self.state.lock().unwrap();
        Ok(FeedResponse::new(
            state.databases.values().cloned().collect(),
        ))
    }

    async fn replace_database(&self, database: CosmosDatabase) -> CosmosResult<CosmosDatabase> {
        let mut state = self.state.lock().unwrap();
        if !state.databases.contains_key(&database.id) {
            return Err(CosmosError::not_found("database", &database.id));
        }
        state
            .databases
            .insert(database.id.clone(), database.clone());
        Ok(database)
    }

    async fn delete_database(&self, id: &str) -> CosmosResult<()> {
        let mut state = self.state.lock().unwrap();
        if state.databases.remove(id).is_none() {
            return Err(CosmosError::not_found("database", id));
        }
        let prefix = format!("{id}/");
        state.containers.retain(|k, _| !k.starts_with(&prefix));
        state.documents.retain(|k, _| !k.starts_with(&prefix));
        Ok(())
    }

    // ---------- Containers ----------

    async fn create_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        let mut state = self.state.lock().unwrap();
        if !state.databases.contains_key(database_id) {
            return Err(CosmosError::not_found("database", database_id));
        }
        let key = Self::coll_key(database_id, &container.id);
        if state.containers.contains_key(&key) {
            return Err(CosmosError::conflict("container", &container.id));
        }
        let mut container = container;
        container.database_id = database_id.to_string();
        state.containers.insert(key.clone(), container.clone());
        state.documents.entry(key).or_default();
        Ok(container)
    }

    async fn get_container(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<CosmosContainer> {
        let state = self.state.lock().unwrap();
        state
            .containers
            .get(&Self::coll_key(database_id, container_id))
            .cloned()
            .ok_or_else(|| CosmosError::not_found("container", container_id))
    }

    async fn list_containers(
        &self,
        database_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosContainer>> {
        let state = self.state.lock().unwrap();
        let prefix = format!("{database_id}/");
        let items = state
            .containers
            .iter()
            .filter(|(k, _)| k.starts_with(&prefix))
            .map(|(_, v)| v.clone())
            .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        let mut state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, &container.id);
        if !state.containers.contains_key(&key) {
            return Err(CosmosError::not_found("container", &container.id));
        }
        state.containers.insert(key, container.clone());
        Ok(container)
    }

    async fn delete_container(&self, database_id: &str, container_id: &str) -> CosmosResult<()> {
        let mut state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        if state.containers.remove(&key).is_none() {
            return Err(CosmosError::not_found("container", container_id));
        }
        state.documents.remove(&key);
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
        let mut state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        let container = state
            .containers
            .get(&key)
            .cloned()
            .ok_or_else(|| CosmosError::not_found("container", container_id))?;
        let id = require_id(&document)?;
        let pk = extract_partition_key(&container, &document);
        let doc_key = Self::doc_key(&pk, &id);
        let coll = state.documents.entry(key).or_default();
        if coll.contains_key(&doc_key) {
            return Err(CosmosError::conflict("document", &id));
        }
        let mut doc = CosmosDocument::new(database_id, container_id, id, pk, document);
        doc.lsn = self.next_lsn();
        doc.is_indexed = is_indexed.unwrap_or(true);
        coll.insert(doc_key, doc.clone());
        self.change_log.record(
            database_id,
            container_id,
            doc.clone(),
            ChangeType::Create,
            None,
        );
        Ok(doc)
    }

    async fn read_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<CosmosDocument> {
        let state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        let doc_key = Self::doc_key(partition_key, document_id);
        state
            .documents
            .get(&key)
            .and_then(|coll| coll.get(&doc_key))
            .cloned()
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
        let mut state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        let container = state
            .containers
            .get(&key)
            .cloned()
            .ok_or_else(|| CosmosError::not_found("container", container_id))?;
        let pk = extract_partition_key(&container, &document);
        let doc_key = Self::doc_key(&pk, document_id);
        let coll = state
            .documents
            .get_mut(&key)
            .ok_or_else(|| CosmosError::not_found("container", container_id))?;
        let existing = coll
            .get(&doc_key)
            .ok_or_else(|| CosmosError::not_found("document", document_id))?;
        if let Some(expected) = if_match {
            if existing.etag != expected {
                return Err(CosmosError::precondition_failed(
                    "ETag does not match for replace.",
                ));
            }
        }
        let previous = existing.clone();
        let mut doc = CosmosDocument::new(database_id, container_id, document_id, pk, document);
        doc.lsn = self.next_lsn();
        doc.etag = etag();
        doc.is_indexed = is_indexed.unwrap_or(true);
        coll.insert(doc_key, doc.clone());
        self.change_log.record(
            database_id,
            container_id,
            doc.clone(),
            ChangeType::Replace,
            Some(previous),
        );
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
        // Try replace, fall back to create.
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
        let mut state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        let doc_key = Self::doc_key(partition_key, document_id);
        let coll = state
            .documents
            .get_mut(&key)
            .ok_or_else(|| CosmosError::not_found("container", container_id))?;
        let existing = coll
            .get(&doc_key)
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
        let previous = existing.clone();
        let mut doc = existing.clone();
        doc.body = body;
        doc.lsn = self.next_lsn();
        doc.etag = etag();
        coll.insert(doc_key, doc.clone());
        self.change_log.record(
            database_id,
            container_id,
            doc.clone(),
            ChangeType::Replace,
            Some(previous),
        );
        Ok(doc)
    }

    async fn delete_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<()> {
        let mut state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        let doc_key = Self::doc_key(partition_key, document_id);
        let coll = state
            .documents
            .get_mut(&key)
            .ok_or_else(|| CosmosError::not_found("container", container_id))?;
        let removed = match coll.remove(&doc_key) {
            Some(doc) => doc,
            None => return Err(CosmosError::not_found("document", document_id)),
        };
        let mut removed = removed;
        removed.lsn = self.next_lsn();
        self.change_log
            .record(database_id, container_id, removed, ChangeType::Delete, None);
        Ok(())
    }

    async fn empty_container(&self, database_id: &str, container_id: &str) -> CosmosResult<usize> {
        let mut state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        let coll = state
            .documents
            .get_mut(&key)
            .ok_or_else(|| CosmosError::not_found("container", container_id))?;
        let count = coll.len();
        coll.clear();
        Ok(count)
    }

    async fn get_global_lsn(&self) -> CosmosResult<i64> {
        Ok(self.global_lsn.load(Ordering::SeqCst))
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
        let state = self.state.lock().unwrap();
        let key = Self::coll_key(database_id, container_id);
        let items = state
            .documents
            .get(&key)
            .map(|coll| coll.values().cloned().collect())
            .unwrap_or_default();
        Ok(FeedResponse::new(items))
    }

    // ---------- Users ----------

    async fn create_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        let mut state = self.state.lock().unwrap();
        let key = format!("{database_id}/{user_id}");
        if state.users.contains_key(&key) {
            return Err(CosmosError::conflict("user", user_id));
        }
        let user = CosmosUser::new(database_id, user_id);
        state.users.insert(key, user.clone());
        Ok(user)
    }

    async fn get_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        let state = self.state.lock().unwrap();
        state
            .users
            .get(&format!("{database_id}/{user_id}"))
            .cloned()
            .ok_or_else(|| CosmosError::not_found("user", user_id))
    }

    async fn list_users(&self, database_id: &str) -> CosmosResult<FeedResponse<CosmosUser>> {
        let state = self.state.lock().unwrap();
        let prefix = format!("{database_id}/");
        let items = state
            .users
            .iter()
            .filter(|(k, _)| k.starts_with(&prefix))
            .map(|(_, v)| v.clone())
            .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_user(&self, database_id: &str, user: CosmosUser) -> CosmosResult<CosmosUser> {
        let mut state = self.state.lock().unwrap();
        let key = format!("{database_id}/{}", user.id);
        if !state.users.contains_key(&key) {
            return Err(CosmosError::not_found("user", &user.id));
        }
        state.users.insert(key, user.clone());
        Ok(user)
    }

    async fn delete_user(&self, database_id: &str, user_id: &str) -> CosmosResult<()> {
        let mut state = self.state.lock().unwrap();
        if state
            .users
            .remove(&format!("{database_id}/{user_id}"))
            .is_none()
        {
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
        let mut state = self.state.lock().unwrap();
        let key = format!("{database_id}/{user_id}/{}", permission.id);
        if state.permissions.contains_key(&key) {
            return Err(CosmosError::conflict("permission", &permission.id));
        }
        state.permissions.insert(key, permission.clone());
        Ok(permission)
    }

    async fn get_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<CosmosPermission> {
        let state = self.state.lock().unwrap();
        state
            .permissions
            .get(&format!("{database_id}/{user_id}/{permission_id}"))
            .cloned()
            .ok_or_else(|| CosmosError::not_found("permission", permission_id))
    }

    async fn list_permissions(
        &self,
        database_id: &str,
        user_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosPermission>> {
        let state = self.state.lock().unwrap();
        let prefix = format!("{database_id}/{user_id}/");
        let items = state
            .permissions
            .iter()
            .filter(|(k, _)| k.starts_with(&prefix))
            .map(|(_, v)| v.clone())
            .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        let mut state = self.state.lock().unwrap();
        let key = format!("{database_id}/{user_id}/{}", permission.id);
        if !state.permissions.contains_key(&key) {
            return Err(CosmosError::not_found("permission", &permission.id));
        }
        state.permissions.insert(key, permission.clone());
        Ok(permission)
    }

    async fn delete_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<()> {
        let mut state = self.state.lock().unwrap();
        if state
            .permissions
            .remove(&format!("{database_id}/{user_id}/{permission_id}"))
            .is_none()
        {
            return Err(CosmosError::not_found("permission", permission_id));
        }
        Ok(())
    }

    // ---------- Offers ----------

    async fn get_offer(&self, offer_id: &str) -> CosmosResult<CosmosOffer> {
        let state = self.state.lock().unwrap();
        state
            .offers
            .get(offer_id)
            .cloned()
            .ok_or_else(|| CosmosError::not_found("offer", offer_id))
    }

    async fn list_offers(&self) -> CosmosResult<FeedResponse<CosmosOffer>> {
        let state = self.state.lock().unwrap();
        Ok(FeedResponse::new(state.offers.values().cloned().collect()))
    }

    async fn replace_offer(&self, offer: CosmosOffer) -> CosmosResult<CosmosOffer> {
        let mut state = self.state.lock().unwrap();
        state.offers.insert(offer.id.clone(), offer.clone());
        Ok(offer)
    }
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

    async fn seed_container(store: &InMemoryDocumentStore) {
        store.create_database("db1").await.unwrap();
        let container =
            CosmosContainer::new("db1", "c1", PartitionKeyDefinition::new(vec!["/pk".into()]));
        store.create_container("db1", container).await.unwrap();
    }

    #[tokio::test]
    async fn create_and_list_database() {
        let store = InMemoryDocumentStore::new();
        store.create_database("db1").await.unwrap();
        let dbs = store.list_databases().await.unwrap();
        assert_eq!(dbs.count(), 1);
        assert_eq!(dbs.resources[0].id, "db1");
    }

    #[tokio::test]
    async fn duplicate_database_conflicts() {
        let store = InMemoryDocumentStore::new();
        store.create_database("db1").await.unwrap();
        let err = store.create_database("db1").await.unwrap_err();
        assert_eq!(err.status_code, 409);
    }

    #[tokio::test]
    async fn document_roundtrip_uses_partition_key() {
        let store = InMemoryDocumentStore::new();
        seed_container(&store).await;
        let created = store
            .create_document("db1", "c1", body("d1", "tenant-a"), None)
            .await
            .unwrap();
        assert_eq!(
            created.partition_key,
            PartitionKeyValue::single(json!("tenant-a"))
        );

        let pk = PartitionKeyValue::single(json!("tenant-a"));
        let read = store.read_document("db1", "c1", "d1", &pk).await.unwrap();
        assert_eq!(read.id, "d1");

        // Wrong partition key => not found (mirrors the point-op 404 gotcha).
        let wrong = PartitionKeyValue::single(json!("tenant-b"));
        let err = store
            .read_document("db1", "c1", "d1", &wrong)
            .await
            .unwrap_err();
        assert_eq!(err.status_code, 404);
    }

    #[tokio::test]
    async fn patch_incr_updates_value() {
        let store = InMemoryDocumentStore::new();
        seed_container(&store).await;
        store
            .create_document("db1", "c1", body("d1", "t"), None)
            .await
            .unwrap();
        let pk = PartitionKeyValue::single(json!("t"));
        let patched = store
            .patch_document(
                "db1",
                "c1",
                "d1",
                &pk,
                &[PatchOperation {
                    op: "incr".into(),
                    path: "/value".into(),
                    value: Some(json!(4)),
                    from: None,
                }],
                None,
                None,
            )
            .await
            .unwrap();
        assert_eq!(patched.body.get("value").unwrap().as_f64().unwrap(), 5.0);
    }
}
