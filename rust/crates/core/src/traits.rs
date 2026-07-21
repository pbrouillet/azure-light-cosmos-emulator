//! Core traits. Ports the interfaces under `src/Core/Interfaces/*`.
//!
//! These are the seams the storage, auth, query, and programmability layers
//! implement. Method sets are stubbed here and completed during the port.

use crate::models::{CosmosContainer, CosmosDatabase, CosmosDocument};
use async_trait::async_trait;

/// Errors surfaced by store operations. Expanded to mirror
/// `CosmosEmulatorException` factory methods (NotFound/Conflict/BadRequest/...).
#[derive(Debug, thiserror::Error)]
pub enum StoreError {
    #[error("resource not found")]
    NotFound,
    #[error("resource conflict")]
    Conflict,
    #[error("bad request: {0}")]
    BadRequest(String),
    #[error(transparent)]
    Other(#[from] anyhow::Error),
}

type Result<T> = std::result::Result<T, StoreError>;

/// Storage abstraction. Ports `IDocumentStore` (31 methods in .NET); the full
/// surface is added incrementally during the `storage-crate` phase.
#[async_trait]
pub trait DocumentStore: Send + Sync {
    async fn list_databases(&self) -> Result<Vec<CosmosDatabase>>;
    async fn create_database(&self, db: CosmosDatabase) -> Result<CosmosDatabase>;
    async fn list_containers(&self, database_id: &str) -> Result<Vec<CosmosContainer>>;
    async fn list_documents(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> Result<Vec<CosmosDocument>>;
}
