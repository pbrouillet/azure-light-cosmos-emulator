//! Partition key ranges (`/dbs/{dbId}/colls/{collId}/pkranges`). Ports
//! `PartitionKeyRangesController`.
//!
//! The emulator never splits partitions, so the routing map is a single static
//! range with a constant ETag. SDK clients drain `/pkranges` as an incremental
//! feed and only stop on HTTP 304, so we honour `If-None-Match` against the
//! stable ETag to avoid infinite client loops.

use axum::extract::{Path, State};
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::Response;
use cosmos_core::models::headers as h;
use serde_json::json;

use crate::response::{empty_response, json_response, HeaderOptions};
use crate::state::AppState;

const ROUTING_MAP_ETAG: &str = "\"00000000-0000-0000-0000-000000000000\"";

pub async fn list(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let if_none_match = headers
        .get(h::IF_NONE_MATCH)
        .and_then(|v| v.to_str().ok())
        .unwrap_or_default();

    let base_options = |item_count: Option<i64>| HeaderOptions {
        request_charge: 1.0,
        database_id: Some(db_id.clone()),
        container_id: Some(coll_id.clone()),
        etag: Some(ROUTING_MAP_ETAG.to_string()),
        item_count,
        ..Default::default()
    };

    if if_none_match == ROUTING_MAP_ETAG {
        return empty_response(&state, StatusCode::NOT_MODIFIED, base_options(None)).await;
    }

    let ts = chrono::Utc::now().timestamp();
    let body = json!({
        "_rid": coll_id,
        "PartitionKeyRanges": [{
            "id": "0",
            "_rid": "0",
            "_self": format!("dbs/{db_id}/colls/{coll_id}/pkranges/0/"),
            "_etag": ROUTING_MAP_ETAG,
            "_ts": ts,
            "minInclusive": "",
            "maxExclusive": "FF",
            "ridPrefix": 0,
            "throughputFraction": 1,
            "status": "online",
            "parents": [],
        }],
        "_count": 1
    });

    let mut response = json_response(&state, StatusCode::OK, base_options(Some(1)), body).await;
    // Ensure the ETag header is present (also set via HeaderOptions).
    if let Ok(value) = header::HeaderValue::from_str(ROUTING_MAP_ETAG) {
        response.headers_mut().insert(header::ETAG, value);
    }
    response
}
