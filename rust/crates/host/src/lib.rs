//! Application host. Ports the .NET `Host` project: assembles storage/auth/query
//! services, builds the middleware pipeline, and serves the Explorer SPA.
//!
//! The Explorer React app is NOT ported — its committed build (`wwwroot/explorer/`)
//! is served statically under `/explorer` via `tower-http`'s `ServeDir`.

use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;

use axum::{routing::get, Json, Router};
use cosmos_core::models::VectorIndexOptions;
use cosmos_core::traits::{DocumentStore, VectorIndexProvider};
use cosmos_core::StorageType;
use cosmos_nosql::AppState;
use cosmos_storage::{
    FlatVectorIndexProvider, InMemoryDocumentStore, SqliteDocumentStore, SurrealDbDocumentStore,
    VectorIndexingDocumentStore,
};
use serde_json::json;

/// Default NoSQL REST API port, matching the .NET emulator.
pub const DEFAULT_PORT: u16 = 8081;

/// Options controlling how the host is built.
pub struct HostOptions {
    pub port: u16,
    /// Storage backend to use. Defaults to [`StorageType::Sqlite`] (the real
    /// default), matching the .NET emulator.
    pub storage: StorageType,
    /// Directory for persistent data (used by the Sqlite/SurrealDb backends).
    pub data_dir: Option<PathBuf>,
    /// Optional directory containing the built Explorer SPA to serve at `/explorer`.
    pub explorer_dir: Option<std::path::PathBuf>,
}

impl Default for HostOptions {
    fn default() -> Self {
        Self {
            port: DEFAULT_PORT,
            storage: StorageType::Sqlite,
            data_dir: None,
            explorer_dir: None,
        }
    }
}

/// Constructs the storage backend selected by [`HostOptions`], wrapped in the
/// vector-indexing decorator (mirroring the .NET DI composition:
/// concrete store → vector index provider → `VectorIndexingDocumentStore`).
pub async fn build_store(opts: &HostOptions) -> Result<Arc<dyn DocumentStore>, anyhow::Error> {
    let inner: Arc<dyn DocumentStore> = match opts.storage {
        StorageType::InMemory => Arc::new(InMemoryDocumentStore::new()),
        StorageType::Sqlite => {
            let dir = opts
                .data_dir
                .clone()
                .unwrap_or_else(|| PathBuf::from("./cosmos-data"));
            Arc::new(SqliteDocumentStore::open(dir)?)
        }
        StorageType::SurrealDb => {
            let dir = opts
                .data_dir
                .clone()
                .unwrap_or_else(|| PathBuf::from("./cosmos-data"));
            Arc::new(SurrealDbDocumentStore::open(dir).await?)
        }
    };
    let index = Arc::new(FlatVectorIndexProvider::new(
        inner.clone(),
        VectorIndexOptions::default(),
    ));
    let store: Arc<dyn DocumentStore> = Arc::new(VectorIndexingDocumentStore::new(
        inner,
        index as Arc<dyn VectorIndexProvider>,
    ));
    Ok(store)
}

/// Builds the top-level Axum router (health + NoSQL API + optional Explorer)
/// around an already-constructed store.
pub fn build_router(opts: &HostOptions, store: Arc<dyn DocumentStore>) -> Router {
    let query_engine = Arc::new(cosmos_query::SqlQueryEngine::new(store.clone()));
    let state = AppState::new(store).with_query_engine(query_engine);

    let mut app = Router::new()
        .route("/health", get(health))
        .merge(cosmos_nosql::router(state));

    if let Some(dir) = &opts.explorer_dir {
        app = app.nest_service("/explorer", tower_http::services::ServeDir::new(dir));
    }

    app
}

async fn health() -> Json<serde_json::Value> {
    Json(json!({ "status": "ok" }))
}

/// Boots the host and serves until shutdown.
pub async fn run(opts: HostOptions) -> Result<(), anyhow::Error> {
    let store = build_store(&opts).await?;
    let app = build_router(&opts, store);
    let addr = SocketAddr::from(([0, 0, 0, 0], opts.port));
    tracing::info!(
        "NoSQL Endpoint: http://localhost:{} (storage: {:?})",
        opts.port,
        opts.storage
    );
    let listener = tokio::net::TcpListener::bind(addr).await?;
    axum::serve(listener, app).await?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;
    use axum::http::{Request, StatusCode};
    use tower::ServiceExt;

    #[tokio::test]
    async fn health_endpoint_returns_ok() {
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(&HostOptions::default(), store);
        let resp = app
            .oneshot(
                Request::builder()
                    .uri("/health")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn dbs_endpoint_is_wired() {
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(&HostOptions::default(), store);
        let resp = app
            .oneshot(Request::builder().uri("/dbs").body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
    }
}
