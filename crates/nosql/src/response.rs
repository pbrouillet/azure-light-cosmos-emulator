//! HTTP response infrastructure: error mapping (ports `CosmosExceptionMiddleware`),
//! common Cosmos response headers (ports `CosmosResponseHeaderService`), request
//! body parsing, and partition-key header parsing.

use axum::body::Body;
use axum::http::{header, HeaderMap, HeaderName, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use cosmos_core::error::CosmosError;
use cosmos_core::models::headers as h;
use cosmos_core::models::{JsonObject, PartitionKeyValue};
use cosmos_core::traits::{ConsistencyManager as _, ProgrammabilityEngine as _};
use serde_json::{json, Value};

use crate::state::AppState;

const RESOURCE_QUOTA: &str =
    "databases=100;collections=25;storedProcedures=100;triggers=25;functions=25;documentSize=10240";

/// An error that renders to a Cosmos JSON error body. Wraps [`CosmosError`] and
/// replaces the .NET exception middleware.
pub struct ApiError(pub CosmosError);

impl From<CosmosError> for ApiError {
    fn from(e: CosmosError) -> Self {
        ApiError(e)
    }
}

impl ApiError {
    pub fn bad_request(message: impl Into<String>) -> Self {
        ApiError(CosmosError::bad_request(message))
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let err = self.0;
        let status =
            StatusCode::from_u16(err.status_code).unwrap_or(StatusCode::INTERNAL_SERVER_ERROR);
        let body = json!({ "code": err.error_code, "message": err.message });
        let mut headers = HeaderMap::new();
        set(
            &mut headers,
            h::REQUEST_CHARGE,
            format!("{:.2}", err.request_charge),
        );
        set(&mut headers, h::ACTIVITY_ID, err.activity_id.clone());
        if let Some(retry) = err.retry_after_ms {
            set(&mut headers, h::RETRY_AFTER_MS, retry.to_string());
        }
        (status, headers, axum::Json(body)).into_response()
    }
}

/// Options controlling the common Cosmos response headers. Ports
/// `CosmosResponseHeaderOptions`.
#[derive(Default)]
pub struct HeaderOptions {
    pub request_charge: f64,
    pub activity_id: Option<String>,
    pub database_id: Option<String>,
    pub container_id: Option<String>,
    pub item_lsn: Option<i64>,
    pub include_session_token: bool,
    pub session_lsn: Option<i64>,
    pub etag: Option<String>,
    pub item_count: Option<i64>,
    pub continuation: Option<String>,
}

impl HeaderOptions {
    pub fn charge(request_charge: f64) -> Self {
        Self {
            request_charge,
            ..Default::default()
        }
    }
}

/// Builds the standard Cosmos response headers. Ports
/// `CosmosResponseHeaderService.ApplyAsync`.
pub async fn common_headers(state: &AppState, options: &HeaderOptions) -> HeaderMap {
    let mut headers = HeaderMap::new();
    let global_lsn = state.store.get_global_lsn().await.unwrap_or(0);
    let resource_usage = build_resource_usage(state).await;

    set(
        &mut headers,
        h::REQUEST_CHARGE,
        format!("{:.2}", options.request_charge),
    );
    set(
        &mut headers,
        h::ACTIVITY_ID,
        options
            .activity_id
            .clone()
            .unwrap_or_else(|| uuid::Uuid::new_v4().to_string()),
    );
    set(
        &mut headers,
        h::SERVICE_VERSION,
        h::CURRENT_SERVICE_VERSION.to_string(),
    );
    set(
        &mut headers,
        h::SCHEMA_VERSION,
        h::CURRENT_SCHEMA_VERSION.to_string(),
    );
    set(&mut headers, h::RESOURCE_QUOTA, RESOURCE_QUOTA.to_string());
    set(&mut headers, h::RESOURCE_USAGE, resource_usage);
    set(
        &mut headers,
        h::GLOBAL_COMMITTED_LSN,
        global_lsn.to_string(),
    );
    set(&mut headers, h::COSMOS_LLSN, global_lsn.to_string());
    set(
        &mut headers,
        h::LAST_STATE_CHANGE_UTC,
        state
            .runtime
            .started_at
            .format("%a, %d %b %Y %H:%M:%S GMT")
            .to_string(),
    );
    set(&mut headers, h::PARTITION_KEY_RANGE_ID, "0".to_string());

    if let Some(lsn) = options.item_lsn {
        set(&mut headers, h::COSMOS_ITEM_LSN, lsn.to_string());
    }

    if options.include_session_token {
        if let (Some(db), Some(coll)) = (&options.database_id, &options.container_id) {
            let token = match options.session_lsn {
                Some(lsn) => state.consistency.generate_session_token(db, coll, lsn),
                None => state.consistency.current_session_token(db, coll),
            };
            set(&mut headers, h::SESSION_TOKEN, token);
        }
    }

    if let Some(etag) = &options.etag {
        if let Ok(value) = HeaderValue::from_str(etag) {
            headers.insert(header::ETAG, value);
        }
    }
    if let Some(count) = options.item_count {
        set(&mut headers, h::ITEM_COUNT, count.to_string());
    }
    if let Some(token) = &options.continuation {
        set(&mut headers, h::CONTINUATION, token.clone());
    }

    headers
}

async fn build_resource_usage(state: &AppState) -> String {
    let databases = state.store.list_databases().await.unwrap_or_default();
    let mut collection_count = 0usize;
    let mut sproc_count = 0usize;
    let mut trigger_count = 0usize;
    let mut udf_count = 0usize;
    for db in &databases.resources {
        if let Ok(containers) = state.store.list_containers(&db.id).await {
            for container in &containers.resources {
                if let Ok(feed) = state
                    .programmability
                    .list_stored_procedures(&db.id, &container.id)
                    .await
                {
                    sproc_count += feed.resources.len();
                }
                if let Ok(feed) = state
                    .programmability
                    .list_triggers(&db.id, &container.id)
                    .await
                {
                    trigger_count += feed.resources.len();
                }
                if let Ok(feed) = state.programmability.list_udfs(&db.id, &container.id).await {
                    udf_count += feed.resources.len();
                }
            }
            collection_count += containers.resources.len();
        }
    }
    format!(
        "databases={};collections={};storedProcedures={};triggers={};functions={};documentSize=0",
        databases.resources.len(),
        collection_count,
        sproc_count,
        trigger_count,
        udf_count
    )
}

/// Assembles a full JSON response with common Cosmos headers.
pub async fn json_response(
    state: &AppState,
    status: StatusCode,
    options: HeaderOptions,
    body: Value,
) -> Response {
    let headers = common_headers(state, &options).await;
    (status, headers, axum::Json(body)).into_response()
}

/// A no-body response (204/304) with common Cosmos headers.
pub async fn empty_response(
    state: &AppState,
    status: StatusCode,
    options: HeaderOptions,
) -> Response {
    let headers = common_headers(state, &options).await;
    let mut response = (status, headers).into_response();
    *response.body_mut() = Body::empty();
    response
}

fn set(headers: &mut HeaderMap, name: &str, value: String) {
    if let (Ok(name), Ok(value)) = (
        HeaderName::from_bytes(name.as_bytes()),
        HeaderValue::from_str(&value),
    ) {
        headers.insert(name, value);
    }
}

/// Parses a request body into a JSON object, mirroring the controllers'
/// `ReadRequestBodyAsync`. Returns `(object, byte_length)`.
pub fn parse_body_object(bytes: &[u8]) -> Result<(JsonObject, usize), ApiError> {
    let value: Value = serde_json::from_slice(bytes)
        .map_err(|_| ApiError::bad_request("Request body must be valid JSON."))?;
    match value {
        Value::Object(map) => Ok((map, bytes.len())),
        _ => Err(ApiError::bad_request("Request body must be a JSON object.")),
    }
}

/// Parses the `x-ms-documentdb-partitionkey` header value (a JSON array) into a
/// [`PartitionKeyValue`]. Ports the controllers' `ParsePartitionKey`.
pub fn parse_partition_key(header: Option<&str>) -> PartitionKeyValue {
    let header = match header {
        Some(h) if !h.is_empty() => h,
        _ => return PartitionKeyValue::undefined(),
    };
    match serde_json::from_str::<Value>(header) {
        Ok(Value::Array(items)) if !items.is_empty() => PartitionKeyValue::multi(items),
        _ => PartitionKeyValue::undefined(),
    }
}
