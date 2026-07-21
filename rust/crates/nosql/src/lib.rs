//! Cosmos DB NoSQL REST API. Ports the .NET `NoSql` project's controllers and
//! middleware to Axum routers and tower layers.
//!
//! URL structure mirrors the Cosmos REST API:
//! `/dbs`, `/dbs/{dbId}/colls`, `/dbs/{dbId}/colls/{collId}/docs`,
//! `.../sprocs`, `.../triggers`, `.../udfs`.
//!
//! Middleware order (parity with .NET): exception → auth → handlers.

use std::sync::Arc;

use axum::{routing::get, Json, Router};
use cosmos_core::traits::DocumentStore;
use serde_json::json;

/// Shared application state passed to handlers.
///
/// Centralizing service construction here avoids the .NET "three DI surfaces"
/// synchronization trap (`Program.cs` / `HostApplication.cs` / test fixture).
#[derive(Clone)]
pub struct AppState {
    pub store: Arc<dyn DocumentStore>,
}

/// Builds the NoSQL REST router. Handlers are added during the `nosql-crate`
/// phase; this scaffold wires the databases listing as a first vertical slice.
pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/dbs", get(list_databases))
        .with_state(state)
}

async fn list_databases(
    axum::extract::State(state): axum::extract::State<AppState>,
) -> Json<serde_json::Value> {
    let dbs = state.store.list_databases().await.unwrap_or_default();
    let ids: Vec<_> = dbs.into_iter().map(|d| json!({ "id": d.id })).collect();
    let count = ids.len();
    Json(json!({ "Databases": ids, "_count": count }))
}
