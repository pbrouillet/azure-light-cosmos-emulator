use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use axum::extract::{Request, State};
use axum::http::{header, HeaderMap, HeaderValue, StatusCode};
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use cosmos_core::models::headers as h;
use cosmos_core::traits::DocumentStore;
use serde_json::json;

#[derive(Clone)]
pub struct ThroughputState {
    store: Arc<dyn DocumentStore>,
    manager: ThroughputManager,
}

impl ThroughputState {
    pub fn new(store: Arc<dyn DocumentStore>) -> Self {
        Self {
            store,
            manager: ThroughputManager::default(),
        }
    }
}

pub async fn enforce(
    State(state): State<ThroughputState>,
    request: Request,
    next: Next,
) -> Response {
    let path = request.uri().path().to_string();
    if should_skip(&path) {
        return next.run(request).await;
    }

    let (Some(database_id), container_id) = extract_resource_ids(&path) else {
        return next.run(request).await;
    };
    let estimated_charge = estimate_charge(request.method().as_str());

    if let Ok(database) = state.store.get_database(&database_id).await {
        if let Some(max_throughput) = database.max_throughput.filter(|v| *v > 0) {
            let retry_after_ms =
                state
                    .manager
                    .try_consume_database(&database_id, max_throughput, estimated_charge);
            if let Some(retry_after_ms) = retry_after_ms {
                tracing::warn!(%database_id, retry_after_ms, "database RU budget exceeded");
                return too_many_requests(retry_after_ms);
            }
        }
    }

    if let Some(container_id) = container_id {
        if let Ok(container) = state.store.get_container(&database_id, &container_id).await {
            if container.max_throughput > 0 {
                let retry_after_ms = state.manager.try_consume(
                    &database_id,
                    &container_id,
                    container.max_throughput,
                    estimated_charge,
                );
                if let Some(retry_after_ms) = retry_after_ms {
                    tracing::warn!(
                        %database_id,
                        %container_id,
                        retry_after_ms,
                        "container RU budget exceeded"
                    );
                    return too_many_requests(retry_after_ms);
                }
            }
        }
    }

    next.run(request).await
}

#[derive(Clone, Default)]
pub struct ThroughputManager {
    budgets: Arc<Mutex<HashMap<String, ContainerBudget>>>,
}

impl ThroughputManager {
    fn try_consume(
        &self,
        database_id: &str,
        container_id: &str,
        provisioned_ru_per_second: i32,
        request_charge: f64,
    ) -> Option<u32> {
        let key = format!("{database_id}/{container_id}");
        self.try_consume_key(&key, provisioned_ru_per_second, request_charge)
    }

    fn try_consume_database(
        &self,
        database_id: &str,
        provisioned_ru_per_second: i32,
        request_charge: f64,
    ) -> Option<u32> {
        let key = format!("db:{database_id}");
        self.try_consume_key(&key, provisioned_ru_per_second, request_charge)
    }

    fn try_consume_key(
        &self,
        key: &str,
        provisioned_ru_per_second: i32,
        request_charge: f64,
    ) -> Option<u32> {
        let limit = provisioned_ru_per_second.max(1) as f64;
        let charge = request_charge.max(0.1);
        let mut budgets = self
            .budgets
            .lock()
            .expect("throughput budget lock poisoned");
        let budget = budgets.entry(key.to_ascii_lowercase()).or_default();
        budget.try_consume(limit, charge)
    }
}

#[derive(Default)]
struct ContainerBudget {
    buckets: VecDeque<(u64, f64)>,
    consumed: f64,
}

impl ContainerBudget {
    fn try_consume(&mut self, limit: f64, charge: f64) -> Option<u32> {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default();
        let current_second = now.as_secs();
        self.trim_expired_buckets(current_second);

        if self.consumed + charge > limit {
            let retry = 1_000u32.saturating_sub(now.subsec_millis()).max(100);
            return Some(retry);
        }

        if let Some((second, existing_charge)) = self.buckets.back_mut() {
            if *second == current_second {
                *existing_charge += charge;
            } else {
                self.buckets.push_back((current_second, charge));
            }
        } else {
            self.buckets.push_back((current_second, charge));
        }
        self.consumed += charge;
        None
    }

    fn trim_expired_buckets(&mut self, current_second: u64) {
        while self
            .buckets
            .front()
            .is_some_and(|(second, _)| *second < current_second)
        {
            if let Some((_, charge)) = self.buckets.pop_front() {
                self.consumed -= charge;
            }
        }
        if self.consumed < 0.0 {
            self.consumed = 0.0;
        }
    }
}

fn should_skip(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    lower.starts_with("/api/") || lower.starts_with("/explorer") || lower.starts_with("/health")
}

fn estimate_charge(method: &str) -> f64 {
    match method {
        "POST" | "PUT" | "DELETE" => 5.0,
        _ => 1.0,
    }
}

fn too_many_requests(retry_after_ms: u32) -> Response {
    let mut headers = HeaderMap::new();
    insert_header(&mut headers, h::RETRY_AFTER_MS, retry_after_ms.to_string());
    insert_header(&mut headers, h::REQUEST_CHARGE, "0.00");
    headers.insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static(h::JSON_CONTENT_TYPE),
    );
    let body = json!({
        "code": "TooManyRequests",
        "message": format!("Request rate is large. Retry after {retry_after_ms} milliseconds.")
    });
    (StatusCode::TOO_MANY_REQUESTS, headers, axum::Json(body)).into_response()
}

fn insert_header(headers: &mut HeaderMap, name: &'static str, value: impl ToString) {
    if let Ok(value) = HeaderValue::from_str(&value.to_string()) {
        headers.insert(name, value);
    }
}

fn extract_resource_ids(path: &str) -> (Option<String>, Option<String>) {
    let segments: Vec<&str> = path
        .split('/')
        .filter(|segment| !segment.is_empty())
        .collect();
    let mut database_id = None;
    let mut container_id = None;

    for (index, segment) in segments.iter().enumerate() {
        if database_id.is_none()
            && segment.eq_ignore_ascii_case("dbs")
            && index + 1 < segments.len()
        {
            database_id = Some(percent_decode(segments[index + 1]));
        }
        if container_id.is_none()
            && segment.eq_ignore_ascii_case("colls")
            && index + 1 < segments.len()
        {
            container_id = Some(percent_decode(segments[index + 1]));
        }
    }

    (database_id, container_id)
}

fn percent_decode(value: &str) -> String {
    let bytes = value.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let Ok(hex) = std::str::from_utf8(&bytes[i + 1..i + 3]) {
                if let Ok(decoded) = u8::from_str_radix(hex, 16) {
                    out.push(decoded);
                    i += 3;
                    continue;
                }
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8(out).unwrap_or_else(|_| value.to_string())
}
