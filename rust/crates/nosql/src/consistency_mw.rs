//! Consistency-level middleware. Ports `ConsistencyMiddleware`.
//!
//! It validates requested consistency overrides and checks session tokens for
//! read/query requests when Session consistency is effective. Like the .NET
//! middleware, a session token ahead of the current LSN is logged but does not
//! reject the request; the emulator returns the currently available data.

use axum::extract::{Request, State};
use axum::http::{Method, StatusCode};
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use cosmos_core::models::headers as h;
use cosmos_core::traits::ConsistencyManager as _;
use cosmos_core::ConsistencyLevel;
use serde_json::json;

use crate::state::AppState;

pub async fn validate(State(state): State<AppState>, mut request: Request, next: Next) -> Response {
    let path = request.uri().path().to_string();
    if should_skip(&path) {
        return next.run(request).await;
    }

    let requested_header = request
        .headers()
        .get(h::CONSISTENCY_LEVEL)
        .and_then(|value| value.to_str().ok())
        .map(str::to_string);
    let requested = requested_header
        .as_deref()
        .and_then(parse_consistency_level);

    if let Some(level) = requested {
        if !state.consistency.is_valid_consistency_level(level) {
            let default = consistency_name(state.consistency.default_consistency_level());
            let requested = requested_header.unwrap_or_default();
            return bad_request(format!(
                "Requested consistency level '{requested}' is stronger than the account default '{default}'. Clients can only request the same or weaker consistency level."
            ));
        }
    }

    let effective = state.consistency.effective_consistency(requested);
    request.extensions_mut().insert(effective);

    if effective == ConsistencyLevel::Session && is_read_or_query(&request) {
        validate_session_token_on_read(&state, &request, &path);
    }

    next.run(request).await
}

fn validate_session_token_on_read(state: &AppState, request: &Request, path: &str) {
    let session_token = request
        .headers()
        .get(h::SESSION_TOKEN)
        .and_then(|value| value.to_str().ok());
    let Some(session_token) = session_token.filter(|token| !token.is_empty()) else {
        return;
    };

    let segments: Vec<&str> = path
        .trim_matches('/')
        .split('/')
        .filter(|segment| !segment.is_empty())
        .collect();
    if segments.len() < 4
        || !segments[0].eq_ignore_ascii_case("dbs")
        || !segments[2].eq_ignore_ascii_case("colls")
    {
        return;
    }

    let database_id = segments[1];
    let container_id = segments[3];
    if !state
        .consistency
        .validate_session_token(database_id, container_id, Some(session_token))
    {
        tracing::warn!(
            session_token,
            database_id,
            container_id,
            "Session token is ahead of current LSN; returning available data"
        );
    }
}

fn is_read_or_query(request: &Request) -> bool {
    if request.method() == Method::GET {
        return true;
    }

    request.method() == Method::POST
        && request
            .headers()
            .get(h::IS_QUERY)
            .and_then(|value| value.to_str().ok())
            .map(|value| value.eq_ignore_ascii_case("true"))
            .unwrap_or(false)
}

fn should_skip(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    lower.starts_with("/explorer")
        || lower == "/"
        || lower.starts_with("/health")
        || lower.starts_with("/api/emulator")
        || lower.starts_with("/swagger")
}

fn parse_consistency_level(value: &str) -> Option<ConsistencyLevel> {
    match value.trim().to_ascii_lowercase().as_str() {
        "strong" => Some(ConsistencyLevel::Strong),
        "boundedstaleness" => Some(ConsistencyLevel::BoundedStaleness),
        "session" => Some(ConsistencyLevel::Session),
        "consistentprefix" => Some(ConsistencyLevel::ConsistentPrefix),
        "eventual" => Some(ConsistencyLevel::Eventual),
        _ => None,
    }
}

fn consistency_name(level: ConsistencyLevel) -> &'static str {
    match level {
        ConsistencyLevel::Strong => "Strong",
        ConsistencyLevel::BoundedStaleness => "BoundedStaleness",
        ConsistencyLevel::Session => "Session",
        ConsistencyLevel::ConsistentPrefix => "ConsistentPrefix",
        ConsistencyLevel::Eventual => "Eventual",
    }
}

fn bad_request(message: String) -> Response {
    (
        StatusCode::BAD_REQUEST,
        axum::Json(json!({
            "code": "BadRequest",
            "message": message,
        })),
    )
        .into_response()
}
