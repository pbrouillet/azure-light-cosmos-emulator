//! Database endpoints (`/dbs`). Ports `DatabasesController`.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::StatusCode;
use axum::response::Response;
use serde_json::json;

use crate::response::{empty_response, json_response, parse_body_object, ApiError, HeaderOptions};
use crate::state::AppState;
use crate::{format, ru};

pub async fn create(State(state): State<AppState>, body: Bytes) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let id = obj.get("id").and_then(|v| v.as_str()).unwrap_or_default();
    if id.is_empty() {
        return Err(ApiError::bad_request("Missing 'id' property."));
    }

    let mut db = state.store.create_database(id).await?;
    if let Some(mt) = obj.get("maxThroughput").and_then(|v| v.as_i64()) {
        db.max_throughput = Some(mt as i32);
    }

    Ok(json_response(
        &state,
        StatusCode::CREATED,
        HeaderOptions::charge(ru::create_database()),
        format::format_database(&db),
    )
    .await)
}

pub async fn list(State(state): State<AppState>) -> Result<Response, ApiError> {
    let feed = state.store.list_databases().await?;
    let items: Vec<_> = feed.resources.iter().map(format::format_database).collect();
    let count = items.len();
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(ru::list_databases()),
        json!({ "_rid": "", "Databases": items, "_count": count }),
    )
    .await)
}

pub async fn get(
    State(state): State<AppState>,
    Path(db_id): Path<String>,
) -> Result<Response, ApiError> {
    let db = state.store.get_database(&db_id).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(ru::get_database()),
        format::format_database(&db),
    )
    .await)
}

pub async fn delete(
    State(state): State<AppState>,
    Path(db_id): Path<String>,
) -> Result<Response, ApiError> {
    state.store.delete_database(&db_id).await?;
    Ok(empty_response(
        &state,
        StatusCode::NO_CONTENT,
        HeaderOptions::charge(ru::delete_database()),
    )
    .await)
}
