//! Authentication middleware. Ports `CosmosAuthMiddleware`.
//!
//! Applied only when [`AppState::auth`] is `Some`. Mirrors the .NET skip list,
//! the local-Explorer bypass, and the case-sensitive `resourceLink` extraction
//! rule required for HMAC signature parity.

use axum::extract::{Request, State};
use axum::http::StatusCode;
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use serde_json::json;

use crate::state::AppState;

pub async fn authenticate(State(state): State<AppState>, request: Request, next: Next) -> Response {
    let auth = match &state.auth {
        Some(auth) => auth.clone(),
        None => return next.run(request).await,
    };

    let path = request.uri().path().to_string();
    if is_skipped(&path) {
        return next.run(request).await;
    }

    let headers = request.headers();
    let auth_header = headers
        .get("Authorization")
        .and_then(|v| v.to_str().ok())
        .unwrap_or_default()
        .to_string();

    if auth_header.is_empty() {
        if is_explorer_request(&request) {
            return next.run(request).await;
        }
        return unauthorized("Missing Authorization header.");
    }

    let verb = request.method().as_str().to_string();
    let (resource_type, resource_link) = extract_resource_info(&path);
    let date_header = headers
        .get("x-ms-date")
        .or_else(|| headers.get("Date"))
        .and_then(|v| v.to_str().ok())
        .unwrap_or_default()
        .to_string();

    let result = auth
        .validate(
            &auth_header,
            &verb,
            &resource_type,
            &resource_link,
            &date_header,
        )
        .await;

    if !result.is_authenticated {
        return unauthorized(result.error_message.as_deref().unwrap_or("Unauthorized."));
    }

    next.run(request).await
}

fn is_skipped(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    lower.starts_with("/explorer")
        || lower == "/"
        || lower.starts_with("/health")
        || lower.starts_with("/api/emulator/explain")
        || lower.starts_with("/api/emulator/throughput")
        || lower.contains("/pkranges")
}

fn is_explorer_request(request: &Request) -> bool {
    let headers = request.headers();
    if let Some(referer) = headers.get("Referer").and_then(|v| v.to_str().ok()) {
        if let Ok(uri) = referer.parse::<axum::http::Uri>() {
            if uri.path().to_ascii_lowercase().starts_with("/explorer") {
                return true;
            }
        }
    }
    headers.get("x-ms-cosmos-explorer").is_some()
}

/// Extracts `(resourceType, resourceLink)` from the request path.
///
/// `resourceType` is lowercased; `resourceLink` preserves its original casing —
/// name-based links are case-sensitive when computing the HMAC signature.
fn extract_resource_info(path: &str) -> (String, String) {
    let segments: Vec<&str> = path
        .trim_matches('/')
        .split('/')
        .filter(|s| !s.is_empty())
        .collect();

    if segments.is_empty() {
        return (String::new(), String::new());
    }

    let resource_type = match segments.len() {
        1 | 2 => segments[0],
        3 | 4 => segments[2],
        5 | 6 => segments[4],
        _ => segments[segments.len() - 1],
    };

    let resource_link = match segments.len() {
        1 => String::new(),
        2 | 3 => segments[..2].join("/"),
        4 | 5 => segments[..4].join("/"),
        6 => segments[..6].join("/"),
        _ => segments.join("/"),
    };

    (resource_type.to_ascii_lowercase(), resource_link)
}

fn unauthorized(message: &str) -> Response {
    let body = json!({ "code": "Unauthorized", "message": message });
    (StatusCode::UNAUTHORIZED, axum::Json(body)).into_response()
}
