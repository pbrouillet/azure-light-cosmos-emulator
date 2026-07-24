//! Permission endpoints (`/dbs/{dbId}/users/{userId}/permissions`). Ports
//! `PermissionsController`.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::StatusCode;
use axum::response::Response;
use cosmos_core::ids::{etag, resource_id};
use cosmos_core::models::{CosmosPermission, PermissionMode};
use serde_json::json;

use crate::format;
use crate::response::{empty_response, json_response, parse_body_object, ApiError, HeaderOptions};
use crate::state::AppState;

fn parse_permission_mode(value: Option<&str>) -> PermissionMode {
    match value {
        Some(v) if v.eq_ignore_ascii_case("Read") => PermissionMode::Read,
        _ => PermissionMode::All,
    }
}

pub async fn create(
    State(state): State<AppState>,
    Path((db_id, user_id)): Path<(String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let id = obj.get("id").and_then(|v| v.as_str()).unwrap_or_default();
    if id.is_empty() {
        return Err(ApiError::bad_request("Missing 'id' property."));
    }
    let permission_mode = parse_permission_mode(obj.get("permissionMode").and_then(|v| v.as_str()));
    let resource = obj
        .get("resource")
        .and_then(|v| v.as_str())
        .unwrap_or_default();
    if resource.is_empty() {
        return Err(ApiError::bad_request("Missing 'resource' property."));
    }

    let permission = CosmosPermission {
        id: id.to_string(),
        rid: resource_id(),
        self_link: String::new(),
        etag: etag(),
        timestamp: chrono::Utc::now().timestamp(),
        database_id: db_id.clone(),
        user_id: user_id.clone(),
        permission_mode,
        resource: resource.to_string(),
        token: None,
    };
    let result = state
        .store
        .create_permission(&db_id, &user_id, permission)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::CREATED,
        HeaderOptions::charge(5.0),
        format::format_permission(&result),
    )
    .await)
}

pub async fn list(
    State(state): State<AppState>,
    Path((db_id, user_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let feed = state.store.list_permissions(&db_id, &user_id).await?;
    let items: Vec<_> = feed
        .resources
        .iter()
        .map(format::format_permission)
        .collect();
    let count = items.len();
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({ "_rid": "", "Permissions": items, "_count": count }),
    )
    .await)
}

pub async fn get(
    State(state): State<AppState>,
    Path((db_id, user_id, permission_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    let permission = state
        .store
        .get_permission(&db_id, &user_id, &permission_id)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        format::format_permission(&permission),
    )
    .await)
}

pub async fn replace(
    State(state): State<AppState>,
    Path((db_id, user_id, permission_id)): Path<(String, String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let permission_mode = parse_permission_mode(obj.get("permissionMode").and_then(|v| v.as_str()));
    let resource = obj.get("resource").and_then(|v| v.as_str());

    let mut existing = state
        .store
        .get_permission(&db_id, &user_id, &permission_id)
        .await?;
    existing.permission_mode = permission_mode;
    if let Some(resource) = resource {
        if !resource.is_empty() {
            existing.resource = resource.to_string();
        }
    }
    let result = state
        .store
        .replace_permission(&db_id, &user_id, existing)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(5.0),
        format::format_permission(&result),
    )
    .await)
}

pub async fn delete(
    State(state): State<AppState>,
    Path((db_id, user_id, permission_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    state
        .store
        .delete_permission(&db_id, &user_id, &permission_id)
        .await?;
    Ok(empty_response(&state, StatusCode::NO_CONTENT, HeaderOptions::charge(5.0)).await)
}
