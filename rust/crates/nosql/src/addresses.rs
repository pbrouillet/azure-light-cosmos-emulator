//! Address resolution endpoint (`/addresses`). Ports `AddressesController`.
//!
//! The emulator exposes a single primary partition address range. SDKs call
//! this endpoint while resolving direct/gateway routing metadata.

use axum::extract::State;
use axum::http::StatusCode;
use axum::response::Response;
use serde_json::json;

use crate::response::{json_response, HeaderOptions};
use crate::state::AppState;

pub async fn get(State(state): State<AppState>) -> Response {
    let scheme = if state.address_endpoint.enable_ssl {
        "https"
    } else {
        "http"
    };
    let port = state.address_endpoint.port;
    let endpoint = format!("{scheme}://localhost:{port}");

    json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({
            "_count": 1,
            "Addresses": [{
                "id": "0",
                "partitionKeyRangeId": "0",
                "protocol": scheme,
                "logicalUri": format!("rntbd://localhost:{port}/"),
                "physicalUri": format!("{endpoint}/"),
                "isPrimary": true,
            }]
        }),
    )
    .await
}
