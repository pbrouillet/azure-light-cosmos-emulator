//! Application host. Ports the .NET `Host` project: assembles storage/auth/query
//! services, builds the middleware pipeline, and serves the Explorer SPA.
//!
//! The Explorer React app is NOT ported — its committed build (`wwwroot/explorer/`)
//! is served statically under `/explorer` via `tower-http`'s `ServeDir`.

use std::net::SocketAddr;
use std::sync::Arc;

use axum::{routing::get, Json, Router};
use cosmos_nosql::AppState;
use cosmos_storage::InMemoryDocumentStore;
use serde_json::json;

/// Default NoSQL REST API port, matching the .NET emulator.
pub const DEFAULT_PORT: u16 = 8081;

/// Options controlling how the host is built.
pub struct HostOptions {
    pub port: u16,
    /// Optional directory containing the built Explorer SPA to serve at `/explorer`.
    pub explorer_dir: Option<std::path::PathBuf>,
}

impl Default for HostOptions {
    fn default() -> Self {
        Self {
            port: DEFAULT_PORT,
            explorer_dir: None,
        }
    }
}

/// Builds the top-level Axum router (health + NoSQL API + optional Explorer).
pub fn build_router(opts: &HostOptions) -> Router {
    let store = Arc::new(InMemoryDocumentStore::new());
    let state = AppState { store };

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
    let app = build_router(&opts);
    let addr = SocketAddr::from(([0, 0, 0, 0], opts.port));
    tracing::info!("NoSQL Endpoint: http://localhost:{}", opts.port);
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
        let app = build_router(&HostOptions::default());
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
        let app = build_router(&HostOptions::default());
        let resp = app
            .oneshot(Request::builder().uri("/dbs").body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
    }
}
