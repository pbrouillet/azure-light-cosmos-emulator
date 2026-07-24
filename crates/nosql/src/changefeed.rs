//! Change feed (`GET /dbs/{dbId}/colls/{collId}/docs/changefeed`). Ports
//! `ChangeFeedController`. Activated by the `A-IM: Incremental feed` header.

use axum::extract::{Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::Response;
use cosmos_core::models::headers as h;
use cosmos_core::traits::ChangeFeedOptions;
use serde_json::{json, Value};

use crate::response::{empty_response, json_response, ApiError, HeaderOptions};
use crate::state::AppState;

pub async fn read(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Result<Response, ApiError> {
    let aim = headers
        .get(h::INCREMENTAL_FEED)
        .and_then(|v| v.to_str().ok())
        .unwrap_or_default();
    if !aim.eq_ignore_ascii_case(h::INCREMENTAL_FEED_VALUE) {
        return Err(ApiError::bad_request(
            "Missing A-IM: Incremental feed header.",
        ));
    }

    let change_feed = state.change_feed.as_ref().ok_or_else(|| {
        ApiError(cosmos_core::error::CosmosError::internal_server_error(
            "Change feed is not enabled.",
        ))
    })?;

    let continuation =
        header_str(&headers, h::CONTINUATION).or_else(|| header_str(&headers, h::IF_NONE_MATCH));
    let max_item_count =
        header_str(&headers, h::MAX_ITEM_COUNT).and_then(|s| s.parse::<i32>().ok());

    let options = ChangeFeedOptions {
        start_from_beginning: continuation.is_none(),
        continuation_token: continuation,
        max_item_count,
        ..Default::default()
    };

    let result = change_feed
        .read_change_feed(&db_id, &coll_id, options)
        .await?;
    let count = result.count();

    let mut header_options = HeaderOptions {
        request_charge: 1.0,
        activity_id: Some(result.activity_id.clone()),
        database_id: Some(db_id),
        container_id: Some(coll_id),
        item_count: Some(count as i64),
        ..Default::default()
    };
    if let Some(token) = &result.continuation_token {
        header_options.continuation = Some(token.clone());
        header_options.etag = Some(token.clone());
    }

    if count == 0 {
        return Ok(empty_response(&state, StatusCode::NOT_MODIFIED, header_options).await);
    }

    let documents: Vec<Value> = result
        .resources
        .iter()
        .map(|item| Value::Object(item.document.to_response_body()))
        .collect();
    Ok(json_response(
        &state,
        StatusCode::OK,
        header_options,
        json!({ "Documents": documents, "_count": count }),
    )
    .await)
}

fn header_str(headers: &HeaderMap, name: &str) -> Option<String> {
    headers
        .get(name)
        .and_then(|v| v.to_str().ok())
        .map(|s| s.to_string())
}
