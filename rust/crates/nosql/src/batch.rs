//! Transactional batch (`POST /dbs/{dbId}/colls/{collId}` with
//! `x-ms-cosmos-is-batch-request: true`). Ports `BatchController`.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use cosmos_core::models::headers as h;
use cosmos_core::models::{BatchOperationRequest, BatchOperationType};
use serde_json::{json, Value};

use crate::response::{json_response, parse_partition_key, ApiError, HeaderOptions};
use crate::state::AppState;

const MAX_BATCH_OPERATIONS: usize = 100;
const MAX_BATCH_REQUEST_SIZE: usize = 2 * 1024 * 1024;

pub async fn execute(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Response, ApiError> {
    let is_batch = headers
        .get(h::IS_BATCH_REQUEST)
        .and_then(|v| v.to_str().ok())
        .map(|v| v.eq_ignore_ascii_case("true"))
        .unwrap_or(false);
    if !is_batch {
        return Ok(StatusCode::NOT_FOUND.into_response());
    }
    run(state, db_id, coll_id, &headers, &body).await
}

/// Executes a transactional batch. Shared by the container-path route
/// (`POST /dbs/{db}/colls/{coll}`) and the docs-path handler
/// (`POST /dbs/{db}/colls/{coll}/docs` with `x-ms-cosmos-is-batch-request: true`,
/// which is the path the official SDKs actually target). Assumes the caller has
/// already confirmed the batch header is present.
pub async fn run(
    state: AppState,
    db_id: String,
    coll_id: String,
    headers: &HeaderMap,
    body: &Bytes,
) -> Result<Response, ApiError> {
    let pk_header = headers.get(h::PARTITION_KEY).and_then(|v| v.to_str().ok());
    let partition_key = parse_partition_key(pk_header);

    if body.len() > MAX_BATCH_REQUEST_SIZE {
        return Err(ApiError::bad_request(format!(
            "Request body size ({} bytes) exceeds the maximum allowed size ({} bytes).",
            body.len(),
            MAX_BATCH_REQUEST_SIZE
        )));
    }

    let value: Value = serde_json::from_slice(body)
        .map_err(|_| ApiError::bad_request("Request body must be a valid JSON array."))?;
    let operations_array = value
        .as_array()
        .ok_or_else(|| ApiError::bad_request("Request body must be a JSON array of operations."))?;

    if operations_array.len() > MAX_BATCH_OPERATIONS {
        return Err(ApiError::bad_request(format!(
            "Batch request contains {} operations, which exceeds the maximum of {}.",
            operations_array.len(),
            MAX_BATCH_OPERATIONS
        )));
    }

    let mut operations = Vec::with_capacity(operations_array.len());
    for (i, op_node) in operations_array.iter().enumerate() {
        let op_obj = op_node.as_object().ok_or_else(|| {
            ApiError::bad_request(format!("Operation at index {i} must be a JSON object."))
        })?;

        let op_type = op_obj
            .get("operationType")
            .and_then(|v| v.as_str())
            .and_then(parse_operation_type)
            .ok_or_else(|| {
                ApiError::bad_request(format!(
                    "Operation at index {i} has an invalid or missing 'operationType'."
                ))
            })?;

        let op_request = BatchOperationRequest {
            operation_type: op_type,
            id: op_obj.get("id").and_then(|v| v.as_str()).map(String::from),
            resource_body: op_obj
                .get("resourceBody")
                .and_then(|v| v.as_object())
                .cloned(),
            if_match: op_obj
                .get("ifMatch")
                .and_then(|v| v.as_str())
                .map(String::from),
            if_none_match: op_obj
                .get("ifNoneMatch")
                .and_then(|v| v.as_str())
                .map(String::from),
        };

        if let Some(message) = validate_operation(i, &op_request) {
            return Err(ApiError::bad_request(message));
        }
        operations.push(op_request);
    }

    let results = state
        .store
        .execute_batch(&db_id, &coll_id, &partition_key, &operations)
        .await?;

    let total_charge: f64 = results.iter().map(|r| r.request_charge).sum();
    let session_lsn = state.store.get_global_lsn().await.unwrap_or(0);

    let response_array: Vec<Value> = results
        .iter()
        .map(|result| {
            let mut obj = serde_json::Map::new();
            obj.insert("statusCode".into(), json!(result.status_code));
            obj.insert("requestCharge".into(), json!(result.request_charge));
            if let Some(body) = &result.resource_body {
                obj.insert("resourceBody".into(), Value::Object(body.clone()));
            }
            if let Some(etag) = &result.etag {
                obj.insert("eTag".into(), json!(etag));
            }
            if let Some(retry) = result.retry_after_ms {
                obj.insert("retryAfterMs".into(), json!(retry));
            }
            Value::Object(obj)
        })
        .collect();

    let options = HeaderOptions {
        request_charge: total_charge,
        database_id: Some(db_id),
        container_id: Some(coll_id),
        include_session_token: true,
        session_lsn: Some(session_lsn),
        ..Default::default()
    };
    Ok(json_response(
        &state,
        StatusCode::OK,
        options,
        Value::Array(response_array),
    )
    .await)
}

fn parse_operation_type(value: &str) -> Option<BatchOperationType> {
    match value.to_ascii_lowercase().as_str() {
        "create" => Some(BatchOperationType::Create),
        "read" => Some(BatchOperationType::Read),
        "replace" => Some(BatchOperationType::Replace),
        "upsert" => Some(BatchOperationType::Upsert),
        "delete" => Some(BatchOperationType::Delete),
        "patch" => Some(BatchOperationType::Patch),
        _ => None,
    }
}

fn validate_operation(index: usize, op: &BatchOperationRequest) -> Option<String> {
    let has_body_id = || {
        op.resource_body
            .as_ref()
            .and_then(|b| b.get("id"))
            .and_then(|v| v.as_str())
            .is_some()
    };
    match op.operation_type {
        BatchOperationType::Create => {
            if op.resource_body.is_none() {
                return Some(format!(
                    "Operation at index {index} (Create) requires a 'resourceBody'."
                ));
            }
            if !has_body_id() {
                return Some(format!(
                    "Operation at index {index} (Create) requires 'resourceBody' to have an 'id' property."
                ));
            }
        }
        BatchOperationType::Read => {
            if op.id.as_deref().unwrap_or_default().is_empty() {
                return Some(format!(
                    "Operation at index {index} (Read) requires an 'id'."
                ));
            }
        }
        BatchOperationType::Replace => {
            if op.id.as_deref().unwrap_or_default().is_empty() {
                return Some(format!(
                    "Operation at index {index} (Replace) requires an 'id'."
                ));
            }
            if op.resource_body.is_none() {
                return Some(format!(
                    "Operation at index {index} (Replace) requires a 'resourceBody'."
                ));
            }
        }
        BatchOperationType::Upsert => {
            if op.resource_body.is_none() {
                return Some(format!(
                    "Operation at index {index} (Upsert) requires a 'resourceBody'."
                ));
            }
            if !has_body_id() {
                return Some(format!(
                    "Operation at index {index} (Upsert) requires 'resourceBody' to have an 'id' property."
                ));
            }
        }
        BatchOperationType::Delete => {
            if op.id.as_deref().unwrap_or_default().is_empty() {
                return Some(format!(
                    "Operation at index {index} (Delete) requires an 'id'."
                ));
            }
        }
        BatchOperationType::Patch => {
            if op.id.as_deref().unwrap_or_default().is_empty() {
                return Some(format!(
                    "Operation at index {index} (Patch) requires an 'id'."
                ));
            }
            if op.resource_body.is_none() {
                return Some(format!(
                    "Operation at index {index} (Patch) requires a 'resourceBody'."
                ));
            }
            let has_ops = op
                .resource_body
                .as_ref()
                .and_then(|b| b.get("operations"))
                .and_then(|v| v.as_array())
                .is_some();
            if !has_ops {
                return Some(format!(
                    "Operation at index {index} (Patch) requires 'resourceBody' to have an 'operations' array."
                ));
            }
        }
    }
    None
}
