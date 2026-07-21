//! Container endpoints (`/dbs/{dbId}/colls`). Ports `ContainersController`.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::StatusCode;
use axum::response::Response;
use cosmos_core::models::CosmosContainer;
use serde_json::json;

use crate::response::{empty_response, json_response, parse_body_object, ApiError, HeaderOptions};
use crate::state::AppState;
use crate::{format, parse, ru};

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
    let pk_node = obj
        .get("partitionKey")
        .ok_or_else(|| ApiError::bad_request("Missing 'partitionKey' property."))?;
    let pk_def = parse::parse_partition_key(pk_node);

    let mut container = CosmosContainer::new(&db_id, id, pk_def);
    if let Some(node) = obj.get("indexingPolicy") {
        container.indexing_policy = parse::parse_indexing_policy(node);
    }
    if let Some(ttl) = obj.get("defaultTtl").and_then(|v| v.as_i64()) {
        container.default_time_to_live = Some(ttl as i32);
    }
    if let Some(mt) = obj.get("maxThroughput").and_then(|v| v.as_i64()) {
        container.max_throughput = mt as i32;
    }
    if let Some(node) = obj.get("uniqueKeyPolicy") {
        container.unique_key_policy = Some(parse::parse_unique_key_policy(node));
    }
    if let Some(node) = obj.get("vectorEmbeddingPolicy") {
        container.vector_embedding_policy = Some(parse::parse_vector_embedding_policy(node));
    }

    let result = state.store.create_container(&db_id, container).await?;
    let options = HeaderOptions {
        request_charge: ru::create_container(),
        database_id: Some(db_id),
        container_id: Some(result.id.clone()),
        include_session_token: true,
        ..Default::default()
    };
    Ok(json_response(
        &state,
        StatusCode::CREATED,
        options,
        format::format_container(&result),
    )
    .await)
}

pub async fn list(
    State(state): State<AppState>,
    Path(db_id): Path<String>,
) -> Result<Response, ApiError> {
    let feed = state.store.list_containers(&db_id).await?;
    let items: Vec<_> = feed
        .resources
        .iter()
        .map(format::format_container)
        .collect();
    let count = items.len();
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(ru::list_containers()),
        json!({ "_rid": "", "DocumentCollections": items, "_count": count }),
    )
    .await)
}

pub async fn get(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let container = state.store.get_container(&db_id, &coll_id).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(ru::get_container()),
        format::format_container(&container),
    )
    .await)
}

pub async fn replace(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let mut existing = state.store.get_container(&db_id, &coll_id).await?;
    if let Some(node) = obj.get("indexingPolicy") {
        existing.indexing_policy = parse::parse_indexing_policy(node);
    }
    if let Some(ttl) = obj.get("defaultTtl").and_then(|v| v.as_i64()) {
        existing.default_time_to_live = Some(ttl as i32);
    }
    if let Some(mt) = obj.get("maxThroughput").and_then(|v| v.as_i64()) {
        existing.max_throughput = mt as i32;
    }
    if let Some(node) = obj.get("vectorEmbeddingPolicy") {
        existing.vector_embedding_policy = Some(parse::parse_vector_embedding_policy(node));
    }

    let result = state.store.replace_container(&db_id, existing).await?;
    let options = HeaderOptions {
        request_charge: 5.0,
        database_id: Some(db_id),
        container_id: Some(result.id.clone()),
        include_session_token: true,
        ..Default::default()
    };
    Ok(json_response(
        &state,
        StatusCode::OK,
        options,
        format::format_container(&result),
    )
    .await)
}

pub async fn delete(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    state.store.delete_container(&db_id, &coll_id).await?;
    let options = HeaderOptions {
        request_charge: ru::delete_container(),
        database_id: Some(db_id),
        container_id: Some(coll_id),
        include_session_token: true,
        ..Default::default()
    };
    Ok(empty_response(&state, StatusCode::NO_CONTENT, options).await)
}
