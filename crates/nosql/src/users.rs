//! User endpoints (`/dbs/{dbId}/users`). Ports `UsersController`.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::StatusCode;
use axum::response::Response;
use serde_json::json;

use crate::format;
use crate::response::{empty_response, json_response, parse_body_object, ApiError, HeaderOptions};
use crate::state::AppState;

pub async fn create(
    State(state): State<AppState>,
    Path(db_id): Path<String>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let id = obj.get("id").and_then(|v| v.as_str()).unwrap_or_default();
    if id.is_empty() {
        return Err(ApiError::bad_request("Missing 'id' property."));
    }
    let user = state.store.create_user(&db_id, id).await?;
    Ok(json_response(
        &state,
        StatusCode::CREATED,
        HeaderOptions::charge(5.0),
        format::format_user(&user),
    )
    .await)
}

pub async fn list(
    State(state): State<AppState>,
    Path(db_id): Path<String>,
) -> Result<Response, ApiError> {
    let feed = state.store.list_users(&db_id).await?;
    let items: Vec<_> = feed.resources.iter().map(format::format_user).collect();
    let count = items.len();
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({ "_rid": "", "Users": items, "_count": count }),
    )
    .await)
}

pub async fn get(
    State(state): State<AppState>,
    Path((db_id, user_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let user = state.store.get_user(&db_id, &user_id).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        format::format_user(&user),
    )
    .await)
}

pub async fn replace(
    State(state): State<AppState>,
    Path((db_id, user_id)): Path<(String, String)>,
    _body: Bytes,
) -> Result<Response, ApiError> {
    let existing = state.store.get_user(&db_id, &user_id).await?;
    let result = state.store.replace_user(&db_id, existing).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(5.0),
        format::format_user(&result),
    )
    .await)
}

pub async fn delete(
    State(state): State<AppState>,
    Path((db_id, user_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    state.store.delete_user(&db_id, &user_id).await?;
    Ok(empty_response(&state, StatusCode::NO_CONTENT, HeaderOptions::charge(5.0)).await)
}
