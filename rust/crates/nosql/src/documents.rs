//! Document endpoints (`/dbs/{dbId}/colls/{collId}/docs`). Ports
//! `DocumentsController` (create/read/replace/upsert/delete/delete-all/patch and
//! the query POST path).
//!
//! The query path uses the injected [`QueryEngine`](cosmos_core::traits::QueryEngine)
//! when present. Until the `query-crate` lands, an absent engine falls back to a
//! naive "return all documents" behaviour (equivalent to `SELECT * FROM c`),
//! which is sufficient for list-style reads and is clearly documented as a stopgap.

use std::collections::HashMap;

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::Response;
use cosmos_core::models::headers as h;
use cosmos_core::models::{JsonObject, PartitionKeyValue, PatchOperation};
use cosmos_core::traits::QueryOptions;
use serde_json::{json, Value};

use crate::response::{
    empty_response, json_response, parse_body_object, parse_partition_key, ApiError, HeaderOptions,
};
use crate::ru;
use crate::state::AppState;

/// POST `/docs` — routes to query / upsert / create based on request headers.
pub async fn create_or_query(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Response, ApiError> {
    if header_is_true(&headers, h::IS_QUERY) {
        return execute_query(&state, &db_id, &coll_id, &headers, &body).await;
    }
    if header_is_true(&headers, h::IS_UPSERT) {
        return upsert(&state, &db_id, &coll_id, &headers, &body).await;
    }
    create(&state, &db_id, &coll_id, &headers, &body).await
}

async fn create(
    state: &AppState,
    db_id: &str,
    coll_id: &str,
    headers: &HeaderMap,
    body: &Bytes,
) -> Result<Response, ApiError> {
    let (obj, request_len) = parse_body_object(body)?;
    let is_indexed = parse_indexing_directive(headers);
    let doc = state
        .store
        .create_document(db_id, coll_id, obj, is_indexed)
        .await?;
    let options = doc_write_options(db_id, coll_id, ru::create(request_len), doc.lsn, &doc.etag);
    Ok(json_response(state, StatusCode::CREATED, options, doc_body(&doc)).await)
}

async fn upsert(
    state: &AppState,
    db_id: &str,
    coll_id: &str,
    headers: &HeaderMap,
    body: &Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(body)?;
    let is_indexed = parse_indexing_directive(headers);
    let charge = ru::upsert(json_len(&obj));
    let doc = state
        .store
        .upsert_document(db_id, coll_id, obj, is_indexed)
        .await?;
    let options = doc_write_options(db_id, coll_id, charge, doc.lsn, &doc.etag);
    Ok(json_response(state, StatusCode::OK, options, doc_body(&doc)).await)
}

/// GET `/docs/{docId}`.
pub async fn read(
    State(state): State<AppState>,
    Path((db_id, coll_id, doc_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let pk = require_partition_key(&headers)?;
    let doc = state
        .store
        .read_document(&db_id, &coll_id, &doc_id, &pk)
        .await?;
    let options = HeaderOptions {
        request_charge: ru::point_read(json_len(&doc.body)),
        database_id: Some(db_id),
        container_id: Some(coll_id),
        item_lsn: Some(doc.lsn),
        etag: Some(doc.etag.clone()),
        ..Default::default()
    };
    Ok(json_response(&state, StatusCode::OK, options, doc_body(&doc)).await)
}

/// PUT `/docs/{docId}`.
pub async fn replace(
    State(state): State<AppState>,
    Path((db_id, coll_id, doc_id)): Path<(String, String, String)>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let if_match = header_str(&headers, h::IF_MATCH);
    let is_indexed = parse_indexing_directive(&headers);
    let charge = ru::replace(json_len(&obj));
    let doc = state
        .store
        .replace_document(
            &db_id,
            &coll_id,
            &doc_id,
            obj,
            if_match.as_deref(),
            is_indexed,
        )
        .await?;
    let options = doc_write_options(&db_id, &coll_id, charge, doc.lsn, &doc.etag);
    Ok(json_response(&state, StatusCode::OK, options, doc_body(&doc)).await)
}

/// DELETE `/docs/{docId}`.
pub async fn delete(
    State(state): State<AppState>,
    Path((db_id, coll_id, doc_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let pk = require_partition_key(&headers)?;
    state
        .store
        .delete_document(&db_id, &coll_id, &doc_id, &pk)
        .await?;
    let session_lsn = state.store.get_global_lsn().await.unwrap_or(0);
    let options = HeaderOptions {
        request_charge: ru::delete(),
        database_id: Some(db_id),
        container_id: Some(coll_id),
        include_session_token: true,
        session_lsn: Some(session_lsn),
        ..Default::default()
    };
    Ok(empty_response(&state, StatusCode::NO_CONTENT, options).await)
}

/// DELETE `/docs` — empties the container.
pub async fn delete_all(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let deleted = state.store.empty_container(&db_id, &coll_id).await?;
    let session_lsn = state.store.get_global_lsn().await.unwrap_or(0);
    let options = HeaderOptions {
        request_charge: ru::delete() * deleted as f64,
        database_id: Some(db_id),
        container_id: Some(coll_id),
        include_session_token: true,
        session_lsn: Some(session_lsn),
        item_count: Some(deleted as i64),
        ..Default::default()
    };
    Ok(empty_response(&state, StatusCode::NO_CONTENT, options).await)
}

/// PATCH `/docs/{docId}`.
pub async fn patch(
    State(state): State<AppState>,
    Path((db_id, coll_id, doc_id)): Path<(String, String, String)>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Response, ApiError> {
    let pk = require_partition_key(&headers)?;
    let if_match = header_str(&headers, h::IF_MATCH);
    let (obj, _) = parse_body_object(&body)?;

    let operations_node = obj.get("operations").and_then(|v| v.as_array());
    let operations_node = match operations_node {
        Some(arr) if !arr.is_empty() => arr,
        _ => {
            return Err(ApiError::bad_request(
                "PATCH request must include a non-empty 'operations' array.",
            ))
        }
    };

    let mut operations = Vec::with_capacity(operations_node.len());
    for op_node in operations_node {
        let op_obj = op_node
            .as_object()
            .ok_or_else(|| ApiError::bad_request("Each operation must be a JSON object."))?;
        let op = op_obj
            .get("op")
            .and_then(|v| v.as_str())
            .unwrap_or_default();
        let path = op_obj
            .get("path")
            .and_then(|v| v.as_str())
            .unwrap_or_default();
        if op.is_empty() || path.is_empty() {
            return Err(ApiError::bad_request(
                "Each operation must have 'op' and 'path' properties.",
            ));
        }
        operations.push(PatchOperation {
            op: op.to_string(),
            path: path.to_string(),
            value: op_obj.get("value").cloned(),
            from: op_obj
                .get("from")
                .and_then(|v| v.as_str())
                .map(String::from),
        });
    }

    let condition = obj.get("condition").and_then(|v| v.as_str());
    let doc = state
        .store
        .patch_document(
            &db_id,
            &coll_id,
            &doc_id,
            &pk,
            &operations,
            if_match.as_deref(),
            condition,
        )
        .await?;
    let charge = ru::replace(json_len(&doc.body));
    let options = doc_write_options(&db_id, &coll_id, charge, doc.lsn, &doc.etag);
    Ok(json_response(&state, StatusCode::OK, options, doc_body(&doc)).await)
}

async fn execute_query(
    state: &AppState,
    db_id: &str,
    coll_id: &str,
    headers: &HeaderMap,
    body: &Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(body)?;
    let query_text = obj
        .get("query")
        .and_then(|v| v.as_str())
        .unwrap_or_default();
    if query_text.is_empty() {
        return Err(ApiError::bad_request("Missing 'query' property."));
    }

    let mut parameters: HashMap<String, Value> = HashMap::new();
    if let Some(params) = obj.get("parameters").and_then(|v| v.as_array()) {
        for param in params {
            if let (Some(name), value) = (
                param.get("name").and_then(|v| v.as_str()),
                param.get("value").cloned(),
            ) {
                parameters.insert(name.to_string(), value.unwrap_or(Value::Null));
            }
        }
    }

    let pk_header = header_str(headers, h::PARTITION_KEY);
    let is_cross_partition = header_is_true(headers, h::ENABLE_CROSS_PARTITION);
    let max_item_count = header_str(headers, h::MAX_ITEM_COUNT).and_then(|s| s.parse::<i32>().ok());
    let continuation = header_str(headers, h::CONTINUATION);
    let enable_scan = header_is_true(headers, h::ENABLE_SCAN);

    let (documents, count, total_size, continuation_out, ru_multiplier) = match &state.query_engine
    {
        Some(engine) => {
            let options = QueryOptions {
                max_item_count,
                continuation_token: continuation,
                enable_cross_partition_query: is_cross_partition,
                partition_key: pk_header.as_deref().map(|s| parse_partition_key(Some(s))),
                enable_scan,
                consistency_level: None,
            };
            let result = engine
                .execute_query(db_id, coll_id, query_text, Some(&parameters), Some(options))
                .await?;
            let total_size: usize = result
                .resources
                .iter()
                .map(|d| serde_json::to_string(d).map(|s| s.len()).unwrap_or(0))
                .sum();
            let count = result.count();
            let docs: Vec<Value> = if result.is_value_projection {
                result
                    .resources
                    .into_iter()
                    .map(|mut o| o.remove("$1").unwrap_or(Value::Null))
                    .collect()
            } else {
                result.resources.into_iter().map(Value::Object).collect()
            };
            (
                docs,
                count,
                total_size,
                result.continuation_token,
                result.ru_multiplier,
            )
        }
        None => {
            // Naive fallback: return all documents (equivalent to SELECT * FROM c).
            let feed = state.store.list_documents(db_id, coll_id).await?;
            let docs: Vec<Value> = feed
                .resources
                .iter()
                .map(|d| Value::Object(d.to_response_body()))
                .collect();
            let total_size: usize = docs
                .iter()
                .map(|d| serde_json::to_string(d).map(|s| s.len()).unwrap_or(0))
                .sum();
            let count = docs.len();
            (docs, count, total_size, None, 1.0)
        }
    };

    let request_charge = ru::query(count, total_size, is_cross_partition, 1, ru_multiplier);
    let options = HeaderOptions {
        request_charge,
        database_id: Some(db_id.to_string()),
        container_id: Some(coll_id.to_string()),
        include_session_token: true,
        item_count: Some(count as i64),
        continuation: continuation_out,
        ..Default::default()
    };
    Ok(json_response(
        state,
        StatusCode::OK,
        options,
        json!({ "_rid": coll_id, "Documents": documents, "_count": count }),
    )
    .await)
}

// ---------- helpers ----------

fn doc_body(doc: &cosmos_core::models::CosmosDocument) -> Value {
    Value::Object(doc.to_response_body())
}

fn doc_write_options(
    db_id: &str,
    coll_id: &str,
    charge: f64,
    lsn: i64,
    etag: &str,
) -> HeaderOptions {
    HeaderOptions {
        request_charge: charge,
        database_id: Some(db_id.to_string()),
        container_id: Some(coll_id.to_string()),
        item_lsn: Some(lsn),
        include_session_token: true,
        session_lsn: Some(lsn),
        etag: Some(etag.to_string()),
        ..Default::default()
    }
}

fn json_len(obj: &JsonObject) -> usize {
    serde_json::to_string(&Value::Object(obj.clone()))
        .map(|s| s.len())
        .unwrap_or(0)
}

fn header_str(headers: &HeaderMap, name: &str) -> Option<String> {
    headers
        .get(name)
        .and_then(|v| v.to_str().ok())
        .map(|s| s.to_string())
}

fn header_is_true(headers: &HeaderMap, name: &str) -> bool {
    header_str(headers, name)
        .map(|v| v.eq_ignore_ascii_case("true"))
        .unwrap_or(false)
}

fn parse_indexing_directive(headers: &HeaderMap) -> Option<bool> {
    match header_str(headers, h::INDEXING_DIRECTIVE)
        .map(|v| v.to_ascii_uppercase())
        .as_deref()
    {
        Some("INCLUDE") => Some(true),
        Some("EXCLUDE") => Some(false),
        _ => None,
    }
}

fn require_partition_key(headers: &HeaderMap) -> Result<PartitionKeyValue, ApiError> {
    let pk_header = header_str(headers, h::PARTITION_KEY);
    let pk_header = match pk_header {
        Some(v) if !v.is_empty() => v,
        _ => {
            return Err(ApiError::bad_request(
                "PartitionKey value must be supplied for this operation.",
            ))
        }
    };
    let parsed = parse_partition_key(Some(&pk_header));
    if parsed.components.is_empty() {
        return Err(ApiError::bad_request(format!(
            "PartitionKey extracted from header is empty. Ensure the '{}' header is a valid JSON array with at least one element, e.g. [\"value\"].",
            h::PARTITION_KEY
        )));
    }
    Ok(parsed)
}
