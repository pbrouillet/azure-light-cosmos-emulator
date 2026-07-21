//! In-memory `DocumentStore` backend. Ports `InMemoryDocumentStore`.
//!
//! Uses composite keys mirroring the .NET layout:
//! databases keyed by `id`, containers by `{db}/{coll}`,
//! documents by `{db}/{coll}/{pk}/{docId}`.

use std::collections::HashMap;
use std::sync::RwLock;

use async_trait::async_trait;
use cosmos_core::models::{CosmosContainer, CosmosDatabase, CosmosDocument};
use cosmos_core::traits::{DocumentStore, StoreError};

#[derive(Default)]
pub struct InMemoryDocumentStore {
    databases: RwLock<HashMap<String, CosmosDatabase>>,
    containers: RwLock<HashMap<String, CosmosContainer>>,
    documents: RwLock<HashMap<String, Vec<CosmosDocument>>>,
}

impl InMemoryDocumentStore {
    pub fn new() -> Self {
        Self::default()
    }
}

#[async_trait]
impl DocumentStore for InMemoryDocumentStore {
    async fn list_databases(&self) -> Result<Vec<CosmosDatabase>, StoreError> {
        Ok(self.databases.read().unwrap().values().cloned().collect())
    }

    async fn create_database(&self, db: CosmosDatabase) -> Result<CosmosDatabase, StoreError> {
        let mut dbs = self.databases.write().unwrap();
        if dbs.contains_key(&db.id) {
            return Err(StoreError::Conflict);
        }
        dbs.insert(db.id.clone(), db.clone());
        Ok(db)
    }

    async fn list_containers(&self, database_id: &str) -> Result<Vec<CosmosContainer>, StoreError> {
        let prefix = format!("{database_id}/");
        Ok(self
            .containers
            .read()
            .unwrap()
            .iter()
            .filter(|(k, _)| k.starts_with(&prefix))
            .map(|(_, v)| v.clone())
            .collect())
    }

    async fn list_documents(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> Result<Vec<CosmosDocument>, StoreError> {
        let key = format!("{database_id}/{container_id}");
        Ok(self
            .documents
            .read()
            .unwrap()
            .get(&key)
            .cloned()
            .unwrap_or_default())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn create_and_list_database() {
        let store = InMemoryDocumentStore::new();
        store
            .create_database(CosmosDatabase { id: "db1".into() })
            .await
            .unwrap();
        let dbs = store.list_databases().await.unwrap();
        assert_eq!(dbs.len(), 1);
        assert_eq!(dbs[0].id, "db1");
    }

    #[tokio::test]
    async fn duplicate_database_conflicts() {
        let store = InMemoryDocumentStore::new();
        store
            .create_database(CosmosDatabase { id: "db1".into() })
            .await
            .unwrap();
        let err = store
            .create_database(CosmosDatabase { id: "db1".into() })
            .await;
        assert!(matches!(err, Err(StoreError::Conflict)));
    }
}
