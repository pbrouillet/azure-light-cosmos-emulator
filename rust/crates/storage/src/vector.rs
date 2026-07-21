//! Vector index provider and the indexing `DocumentStore` decorator.
//!
//! Ports `HnswVectorIndexProvider` and `VectorIndexingDocumentStore`.
//!
//! The .NET provider uses HNSW.Net for approximate search on large shards and
//! falls back to an **exact ("flat") scan** for small partitions and while the
//! graph builds. This Rust port implements the exact-scan path only, which
//! yields identical (fully correct) results; the approximate HNSW graph is a
//! deferred optimization. Because search is exact, the tombstone/rebuild/
//! background-build machinery is unnecessary — entries are stored in a map and
//! removed directly.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use cosmos_core::error::CosmosResult;
use cosmos_core::models::vector::vector_math;
use cosmos_core::models::*;
use cosmos_core::traits::{DocumentStore, VectorIndexProvider};
use serde_json::Value;

/// A single indexed embedding.
#[derive(Clone)]
struct Entry {
    doc_id: String,
    pk: PartitionKeyValue,
    vector: Vec<f32>,
}

/// An index for one container embedding path.
struct Shard {
    path: String,
    distance_function: VectorDistanceFunction,
    /// Vector length; `0` until the first embedding is seen.
    dimensions: usize,
    /// Keyed by `DocKey` = `{pkHeader}\0{docId}`.
    entries: HashMap<String, Entry>,
}

/// In-memory exact ("flat") vector index provider. Shards are built lazily from
/// the backing store on first use and kept current via the maintenance hooks
/// invoked by [`VectorIndexingDocumentStore`].
pub struct FlatVectorIndexProvider {
    store: Arc<dyn DocumentStore>,
    options: VectorIndexOptions,
    shards: Mutex<HashMap<String, Shard>>,
}

impl FlatVectorIndexProvider {
    pub fn new(store: Arc<dyn DocumentStore>, options: VectorIndexOptions) -> Self {
        Self {
            store,
            options,
            shards: Mutex::new(HashMap::new()),
        }
    }

    fn normalize_path(path: &str) -> String {
        format!("/{}", path.trim().trim_start_matches('/'))
    }

    fn shard_key(database_id: &str, container_id: &str, path: &str) -> String {
        format!("{database_id}\0{container_id}\0{path}")
    }

    fn shard_key_prefix(database_id: &str, container_id: &str) -> String {
        format!("{database_id}\0{container_id}\0")
    }

    fn doc_key(doc_id: &str, pk: &PartitionKeyValue) -> String {
        format!("{}\0{doc_id}", pk.to_header_string())
    }

    fn paths_match(a: &str, b: &str) -> bool {
        Self::normalize_path(a) == Self::normalize_path(b)
    }

    /// Extracts a float embedding from `body` at `path`. Returns `None` when the
    /// path is missing, is not a non-empty array, or contains a non-numeric item.
    fn extract_vector(body: &JsonObject, path: &str) -> Option<Vec<f32>> {
        let mut node = Value::Object(body.clone());
        for segment in path.split('/').filter(|s| !s.is_empty()) {
            node = node.get(segment)?.clone();
        }
        let array = node.as_array()?;
        if array.is_empty() {
            return None;
        }
        let mut vector = Vec::with_capacity(array.len());
        for item in array {
            vector.push(item.as_f64()? as f32);
        }
        Some(vector)
    }

    /// Builds a shard from the store's current documents. Returns `None` when the
    /// path is not indexable (no declared policy and implicit indexing disabled).
    async fn build_shard(
        &self,
        database_id: &str,
        container_id: &str,
        path: &str,
        _index_type: &str,
        distance_function: VectorDistanceFunction,
    ) -> CosmosResult<Option<Shard>> {
        let container = self.store.get_container(database_id, container_id).await?;

        let declared = container
            .indexing_policy
            .vector_indexes
            .as_ref()
            .and_then(|list| list.iter().find(|vi| Self::paths_match(&vi.path, path)));
        if !self.options.implicit_indexing && declared.is_none() {
            return Ok(None);
        }

        // Prefer an explicitly declared embedding policy's distance function. Only
        // the exact ("flat") path is implemented, so the declared index type does
        // not affect search behavior.
        let mut effective_function = distance_function;
        if let Some(policy) = container.vector_embedding_policy.as_ref() {
            if let Some(embedding) = policy
                .vector_embeddings
                .iter()
                .find(|e| Self::paths_match(&e.path, path))
            {
                effective_function =
                    VectorDistanceFunction::parse(Some(&embedding.distance_function));
            }
        }

        let mut shard = Shard {
            path: path.to_string(),
            distance_function: effective_function,
            dimensions: 0,
            entries: HashMap::new(),
        };

        let docs = self.store.list_documents(database_id, container_id).await?;
        for doc in docs.resources {
            let Some(vector) = Self::extract_vector(&doc.body, path) else {
                continue;
            };
            if shard.dimensions == 0 {
                shard.dimensions = vector.len();
            } else if vector.len() != shard.dimensions {
                continue;
            }
            let key = Self::doc_key(&doc.id, &doc.partition_key);
            shard.entries.insert(
                key,
                Entry {
                    doc_id: doc.id.clone(),
                    pk: doc.partition_key.clone(),
                    vector,
                },
            );
        }

        Ok(Some(shard))
    }

    /// Applies a mutation to every built shard of a container under the lock.
    fn for_built_shards(
        &self,
        database_id: &str,
        container_id: &str,
        mut f: impl FnMut(&mut Shard),
    ) {
        let prefix = Self::shard_key_prefix(database_id, container_id);
        let mut shards = self.shards.lock().unwrap();
        for (key, shard) in shards.iter_mut() {
            if key.starts_with(&prefix) {
                f(shard);
            }
        }
    }
}

#[async_trait]
impl VectorIndexProvider for FlatVectorIndexProvider {
    fn is_enabled(&self) -> bool {
        self.options.enabled
    }

    async fn ensure_index(
        &self,
        database_id: &str,
        container_id: &str,
        path: &str,
        index_type: &str,
        distance_function: VectorDistanceFunction,
    ) -> CosmosResult<bool> {
        if !self.options.enabled {
            return Ok(false);
        }
        let normalized = Self::normalize_path(path);
        let key = Self::shard_key(database_id, container_id, &normalized);

        if self.shards.lock().unwrap().contains_key(&key) {
            return Ok(true);
        }

        // Build without holding the lock (the build reads from the store).
        let shard = self
            .build_shard(
                database_id,
                container_id,
                &normalized,
                index_type,
                distance_function,
            )
            .await?;
        match shard {
            Some(shard) => {
                self.shards.lock().unwrap().insert(key, shard);
                Ok(true)
            }
            None => Ok(false),
        }
    }

    async fn search(&self, request: VectorSearchRequest) -> CosmosResult<Vec<VectorHit>> {
        if !self.options.enabled {
            return Ok(Vec::new());
        }
        let built = self
            .ensure_index(
                &request.database_id,
                &request.container_id,
                &request.path,
                &request.index_type,
                request.distance_function,
            )
            .await?;
        if !built {
            return Ok(Vec::new());
        }

        let normalized = Self::normalize_path(&request.path);
        let key = Self::shard_key(&request.database_id, &request.container_id, &normalized);
        let shards = self.shards.lock().unwrap();
        let Some(shard) = shards.get(&key) else {
            return Ok(Vec::new());
        };

        let query = &request.query_vector;
        if shard.dimensions == 0 || query.len() != shard.dimensions {
            return Ok(Vec::new());
        }

        let pk_header = request
            .partition_key
            .as_ref()
            .map(|pk| pk.to_header_string());
        let mut ranked: Vec<(&Entry, f64)> = shard
            .entries
            .values()
            .filter(|e| match &pk_header {
                Some(h) => &e.pk.to_header_string() == h,
                None => true,
            })
            .map(|e| {
                (
                    e,
                    vector_math::nearest_first_distance(&e.vector, query, shard.distance_function),
                )
            })
            .collect();

        ranked.sort_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(std::cmp::Ordering::Equal));
        ranked.truncate(request.top_k);

        let hits = ranked
            .into_iter()
            .map(|(e, distance)| VectorHit {
                document_id: e.doc_id.clone(),
                partition_key: e.pk.clone(),
                distance,
                score: vector_math::score(&e.vector, query, shard.distance_function),
            })
            .collect();
        Ok(hits)
    }

    fn on_upsert(&self, database_id: &str, container_id: &str, document: &CosmosDocument) {
        let doc_id = document.id.clone();
        let pk = document.partition_key.clone();
        let body = document.body.clone();
        self.for_built_shards(database_id, container_id, |shard| {
            let doc_key = Self::doc_key(&doc_id, &pk);
            shard.entries.remove(&doc_key);
            if let Some(vector) = Self::extract_vector(&body, &shard.path) {
                if shard.dimensions == 0 {
                    shard.dimensions = vector.len();
                }
                if vector.len() == shard.dimensions {
                    shard.entries.insert(
                        doc_key,
                        Entry {
                            doc_id: doc_id.clone(),
                            pk: pk.clone(),
                            vector,
                        },
                    );
                }
            }
        });
    }

    fn on_delete(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) {
        let doc_key = Self::doc_key(document_id, partition_key);
        self.for_built_shards(database_id, container_id, |shard| {
            shard.entries.remove(&doc_key);
        });
    }

    fn on_container_cleared(&self, database_id: &str, container_id: &str) {
        self.for_built_shards(database_id, container_id, |shard| {
            shard.entries.clear();
        });
    }

    fn on_container_dropped(&self, database_id: &str, container_id: &str) {
        let prefix = Self::shard_key_prefix(database_id, container_id);
        self.shards
            .lock()
            .unwrap()
            .retain(|key, _| !key.starts_with(&prefix));
    }
}

/// A [`DocumentStore`] decorator that keeps a [`VectorIndexProvider`] in sync
/// with document mutations. Ports `VectorIndexingDocumentStore`. All storage is
/// delegated to the wrapped inner store; only document- and container-mutating
/// operations additionally notify the index. Works for any backing store.
pub struct VectorIndexingDocumentStore {
    inner: Arc<dyn DocumentStore>,
    index: Arc<dyn VectorIndexProvider>,
}

impl VectorIndexingDocumentStore {
    pub fn new(inner: Arc<dyn DocumentStore>, index: Arc<dyn VectorIndexProvider>) -> Self {
        Self { inner, index }
    }
}

#[async_trait]
impl DocumentStore for VectorIndexingDocumentStore {
    // ---- Databases (pass-through) ----
    async fn create_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        self.inner.create_database(id).await
    }
    async fn get_database(&self, id: &str) -> CosmosResult<CosmosDatabase> {
        self.inner.get_database(id).await
    }
    async fn list_databases(&self) -> CosmosResult<FeedResponse<CosmosDatabase>> {
        self.inner.list_databases().await
    }
    async fn replace_database(&self, database: CosmosDatabase) -> CosmosResult<CosmosDatabase> {
        self.inner.replace_database(database).await
    }
    async fn delete_database(&self, id: &str) -> CosmosResult<()> {
        self.inner.delete_database(id).await
    }

    // ---- Containers ----
    async fn create_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        self.inner.create_container(database_id, container).await
    }
    async fn get_container(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<CosmosContainer> {
        self.inner.get_container(database_id, container_id).await
    }
    async fn list_containers(
        &self,
        database_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosContainer>> {
        self.inner.list_containers(database_id).await
    }
    async fn replace_container(
        &self,
        database_id: &str,
        container: CosmosContainer,
    ) -> CosmosResult<CosmosContainer> {
        let container_id = container.id.clone();
        let result = self.inner.replace_container(database_id, container).await?;
        // Indexing/embedding policy may have changed; drop shards so they rebuild lazily.
        self.index.on_container_dropped(database_id, &container_id);
        Ok(result)
    }
    async fn delete_container(&self, database_id: &str, container_id: &str) -> CosmosResult<()> {
        self.inner
            .delete_container(database_id, container_id)
            .await?;
        self.index.on_container_dropped(database_id, container_id);
        Ok(())
    }

    // ---- Documents ----
    async fn create_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let doc = self
            .inner
            .create_document(database_id, container_id, document, is_indexed)
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
    }
    async fn read_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<CosmosDocument> {
        self.inner
            .read_document(database_id, container_id, document_id, partition_key)
            .await
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
        let doc = self
            .inner
            .replace_document(
                database_id,
                container_id,
                document_id,
                document,
                if_match,
                is_indexed,
            )
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
    }
    async fn upsert_document(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        is_indexed: Option<bool>,
    ) -> CosmosResult<CosmosDocument> {
        let doc = self
            .inner
            .upsert_document(database_id, container_id, document, is_indexed)
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
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
        condition: Option<&str>,
    ) -> CosmosResult<CosmosDocument> {
        let doc = self
            .inner
            .patch_document(
                database_id,
                container_id,
                document_id,
                partition_key,
                operations,
                if_match,
                condition,
            )
            .await?;
        self.index.on_upsert(database_id, container_id, &doc);
        Ok(doc)
    }
    async fn delete_document(
        &self,
        database_id: &str,
        container_id: &str,
        document_id: &str,
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<()> {
        self.inner
            .delete_document(database_id, container_id, document_id, partition_key)
            .await?;
        self.index
            .on_delete(database_id, container_id, document_id, partition_key);
        Ok(())
    }
    async fn empty_container(&self, database_id: &str, container_id: &str) -> CosmosResult<usize> {
        let count = self
            .inner
            .empty_container(database_id, container_id)
            .await?;
        self.index.on_container_cleared(database_id, container_id);
        Ok(count)
    }
    async fn get_global_lsn(&self) -> CosmosResult<i64> {
        self.inner.get_global_lsn().await
    }

    // ---- Batch ----
    async fn execute_batch(
        &self,
        database_id: &str,
        container_id: &str,
        partition_key: &PartitionKeyValue,
        operations: &[BatchOperationRequest],
    ) -> CosmosResult<Vec<BatchOperationResponse>> {
        let responses = self
            .inner
            .execute_batch(database_id, container_id, partition_key, operations)
            .await?;

        for (op, response) in operations.iter().zip(responses.iter()) {
            if !(200..300).contains(&response.status_code) {
                continue;
            }
            match op.operation_type {
                BatchOperationType::Create
                | BatchOperationType::Replace
                | BatchOperationType::Upsert
                | BatchOperationType::Patch => {
                    let id = op.id.clone().or_else(|| {
                        response
                            .resource_body
                            .as_ref()
                            .and_then(|b| b.get("id"))
                            .and_then(|v| v.as_str())
                            .map(|s| s.to_string())
                    });
                    if let Some(id) = id {
                        if let Ok(doc) = self
                            .inner
                            .read_document(database_id, container_id, &id, partition_key)
                            .await
                        {
                            self.index.on_upsert(database_id, container_id, &doc);
                        }
                    }
                }
                BatchOperationType::Delete => {
                    if let Some(id) = op.id.as_deref() {
                        self.index
                            .on_delete(database_id, container_id, id, partition_key);
                    }
                }
                BatchOperationType::Read => {}
            }
        }

        Ok(responses)
    }

    // ---- Bulk reads (pass-through) ----
    async fn read_many_documents(
        &self,
        database_id: &str,
        container_id: &str,
        items: &[(String, PartitionKeyValue)],
    ) -> CosmosResult<FeedResponse<CosmosDocument>> {
        self.inner
            .read_many_documents(database_id, container_id, items)
            .await
    }
    async fn list_documents(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosDocument>> {
        self.inner.list_documents(database_id, container_id).await
    }

    // ---- Users (pass-through) ----
    async fn create_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        self.inner.create_user(database_id, user_id).await
    }
    async fn get_user(&self, database_id: &str, user_id: &str) -> CosmosResult<CosmosUser> {
        self.inner.get_user(database_id, user_id).await
    }
    async fn list_users(&self, database_id: &str) -> CosmosResult<FeedResponse<CosmosUser>> {
        self.inner.list_users(database_id).await
    }
    async fn replace_user(&self, database_id: &str, user: CosmosUser) -> CosmosResult<CosmosUser> {
        self.inner.replace_user(database_id, user).await
    }
    async fn delete_user(&self, database_id: &str, user_id: &str) -> CosmosResult<()> {
        self.inner.delete_user(database_id, user_id).await
    }

    // ---- Permissions (pass-through) ----
    async fn create_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        self.inner
            .create_permission(database_id, user_id, permission)
            .await
    }
    async fn get_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<CosmosPermission> {
        self.inner
            .get_permission(database_id, user_id, permission_id)
            .await
    }
    async fn list_permissions(
        &self,
        database_id: &str,
        user_id: &str,
    ) -> CosmosResult<FeedResponse<CosmosPermission>> {
        self.inner.list_permissions(database_id, user_id).await
    }
    async fn replace_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission: CosmosPermission,
    ) -> CosmosResult<CosmosPermission> {
        self.inner
            .replace_permission(database_id, user_id, permission)
            .await
    }
    async fn delete_permission(
        &self,
        database_id: &str,
        user_id: &str,
        permission_id: &str,
    ) -> CosmosResult<()> {
        self.inner
            .delete_permission(database_id, user_id, permission_id)
            .await
    }

    // ---- Offers (pass-through) ----
    async fn get_offer(&self, offer_id: &str) -> CosmosResult<CosmosOffer> {
        self.inner.get_offer(offer_id).await
    }
    async fn list_offers(&self) -> CosmosResult<FeedResponse<CosmosOffer>> {
        self.inner.list_offers().await
    }
    async fn replace_offer(&self, offer: CosmosOffer) -> CosmosResult<CosmosOffer> {
        self.inner.replace_offer(offer).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::inmemory::InMemoryDocumentStore;
    use cosmos_core::models::policies::VectorIndex;
    use serde_json::json;

    fn build() -> (
        Arc<VectorIndexingDocumentStore>,
        Arc<FlatVectorIndexProvider>,
    ) {
        let inner: Arc<dyn DocumentStore> = Arc::new(InMemoryDocumentStore::new());
        let provider = Arc::new(FlatVectorIndexProvider::new(
            inner.clone(),
            VectorIndexOptions::default(),
        ));
        let store = Arc::new(VectorIndexingDocumentStore::new(
            inner,
            provider.clone() as Arc<dyn VectorIndexProvider>,
        ));
        (store, provider)
    }

    async fn seed(store: &VectorIndexingDocumentStore) {
        store.create_database("db1").await.unwrap();
        let mut container =
            CosmosContainer::new("db1", "c1", PartitionKeyDefinition::new(vec!["/pk".into()]));
        container.indexing_policy.vector_indexes = Some(vec![VectorIndex {
            path: "/embedding".into(),
            index_type: "flat".into(),
        }]);
        store.create_container("db1", container).await.unwrap();
    }

    fn doc(id: &str, pk: &str, embedding: [f32; 2]) -> JsonObject {
        json!({ "id": id, "pk": pk, "embedding": [embedding[0], embedding[1]] })
            .as_object()
            .unwrap()
            .clone()
    }

    fn request(query: Vec<f32>, top_k: usize) -> VectorSearchRequest {
        VectorSearchRequest {
            database_id: "db1".into(),
            container_id: "c1".into(),
            path: "/embedding".into(),
            query_vector: query,
            distance_function: VectorDistanceFunction::Cosine,
            top_k,
            partition_key: None,
            index_type: "flat".into(),
        }
    }

    #[tokio::test]
    async fn search_returns_nearest_first() {
        let (store, provider) = build();
        seed(&store).await;
        store
            .create_document("db1", "c1", doc("a", "p1", [1.0, 0.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("b", "p1", [0.0, 1.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("c", "p1", [0.9, 0.1]), None)
            .await
            .unwrap();

        let hits = provider.search(request(vec![1.0, 0.0], 3)).await.unwrap();
        assert_eq!(hits.len(), 3);
        // Nearest to [1,0] is "a" (identical), then "c", then "b".
        assert_eq!(hits[0].document_id, "a");
        assert_eq!(hits[1].document_id, "c");
        assert_eq!(hits[2].document_id, "b");
        assert!(hits[0].distance <= hits[1].distance);
    }

    #[tokio::test]
    async fn top_k_limits_results() {
        let (store, provider) = build();
        seed(&store).await;
        for (i, id) in ["a", "b", "c", "d"].iter().enumerate() {
            store
                .create_document("db1", "c1", doc(id, "p1", [i as f32, 1.0]), None)
                .await
                .unwrap();
        }
        let hits = provider.search(request(vec![0.0, 1.0], 2)).await.unwrap();
        assert_eq!(hits.len(), 2);
    }

    #[tokio::test]
    async fn delete_removes_from_index() {
        let (store, provider) = build();
        seed(&store).await;
        store
            .create_document("db1", "c1", doc("a", "p1", [1.0, 0.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("b", "p1", [0.0, 1.0]), None)
            .await
            .unwrap();
        // Build the shard, then delete.
        let _ = provider.search(request(vec![1.0, 0.0], 5)).await.unwrap();
        store
            .delete_document("db1", "c1", "a", &PartitionKeyValue::single(json!("p1")))
            .await
            .unwrap();
        let hits = provider.search(request(vec![1.0, 0.0], 5)).await.unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].document_id, "b");
    }

    #[tokio::test]
    async fn partition_scope_filters_results() {
        let (store, provider) = build();
        seed(&store).await;
        store
            .create_document("db1", "c1", doc("a", "p1", [1.0, 0.0]), None)
            .await
            .unwrap();
        store
            .create_document("db1", "c1", doc("b", "p2", [0.9, 0.1]), None)
            .await
            .unwrap();

        let mut req = request(vec![1.0, 0.0], 5);
        req.partition_key = Some(PartitionKeyValue::single(json!("p2")));
        let hits = provider.search(req).await.unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].document_id, "b");
    }

    #[tokio::test]
    async fn disabled_provider_returns_empty() {
        let inner: Arc<dyn DocumentStore> = Arc::new(InMemoryDocumentStore::new());
        let options = VectorIndexOptions {
            enabled: false,
            ..Default::default()
        };
        let provider = FlatVectorIndexProvider::new(inner, options);
        assert!(!provider.is_enabled());
        let hits = provider.search(request(vec![1.0, 0.0], 5)).await.unwrap();
        assert!(hits.is_empty());
    }
}
