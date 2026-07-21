//! Offer endpoints (`/offers`). Ports `OffersController`.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::StatusCode;
use axum::response::Response;
use serde_json::json;

use crate::format;
use crate::response::{json_response, parse_body_object, ApiError, HeaderOptions};
use crate::state::AppState;

pub async fn list(State(state): State<AppState>) -> Result<Response, ApiError> {
    let feed = state.store.list_offers().await?;
    let items: Vec<_> = feed.resources.iter().map(format::format_offer).collect();
    let count = items.len();
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({ "_rid": "", "Offers": items, "_count": count }),
    )
    .await)
}

/// POST `/offers` — treated as a query that returns all offers.
pub async fn query(State(state): State<AppState>, _body: Bytes) -> Result<Response, ApiError> {
    list(State(state)).await
}

pub async fn get(
    State(state): State<AppState>,
    Path(offer_id): Path<String>,
) -> Result<Response, ApiError> {
    let offer = state.store.get_offer(&offer_id).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        format::format_offer(&offer),
    )
    .await)
}

pub async fn replace(
    State(state): State<AppState>,
    Path(offer_id): Path<String>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let mut existing = state.store.get_offer(&offer_id).await?;
    if let Some(throughput) = obj
        .get("content")
        .and_then(|c| c.get("offerThroughput"))
        .and_then(|v| v.as_i64())
    {
        existing.content.offer_throughput = throughput as i32;
    }
    let result = state.store.replace_offer(existing).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(5.0),
        format::format_offer(&result),
    )
    .await)
}
