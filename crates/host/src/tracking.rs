use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use axum::extract::{Request, State};
use axum::http::{HeaderMap, HeaderValue};
use axum::middleware::Next;
use axum::response::Response;
use cosmos_core::models::{headers as h, ActivityEntry};
use cosmos_core::traits::ActivityStore;
use serde::Serialize;

#[derive(Clone, Default)]
pub struct TrackingState {
    tracker: RuTracker,
    activity_store: Option<Arc<dyn ActivityStore>>,
}

impl TrackingState {
    pub fn new(activity_store: Option<Arc<dyn ActivityStore>>) -> Self {
        Self {
            tracker: RuTracker::default(),
            activity_store,
        }
    }
}

pub async fn track(State(state): State<TrackingState>, request: Request, next: Next) -> Response {
    let path = request.uri().path().to_string();
    if !should_track(&path) {
        return next.run(request).await;
    }

    let start = Instant::now();
    let method = request.method().as_str().to_string();
    let (database_id, container_id) = extract_resource_ids(&path);
    let mut response = next.run(request).await;
    let latency_ms = (start.elapsed().as_secs_f64() * 1_000.0 * 100.0).round() / 100.0;
    let status = response.status().as_u16();
    let request_charge = parse_request_charge(response.headers());
    let activity_id = ensure_activity_id(response.headers_mut());

    let diagnostics = Diagnostics {
        latency_ms,
        request_charge: (request_charge * 100.0).round() / 100.0,
        partition_id: "0",
        activity_id: &activity_id,
    };
    if let Ok(value) = serde_json::to_string(&diagnostics) {
        insert_header(response.headers_mut(), h::DIAGNOSTICS, value);
    }

    let entry = ActivityEntry {
        timestamp: chrono::Utc::now(),
        method,
        path,
        status_code: i32::from(status),
        request_charge,
        latency_ms,
        database_id,
        container_id,
    };

    state.tracker.record_request(entry.clone());
    if let Some(activity_store) = &state.activity_store {
        if let Err(error) = activity_store.record(entry).await {
            tracing::debug!(%error, "failed to persist activity entry");
        }
    }

    response
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Diagnostics<'a> {
    latency_ms: f64,
    request_charge: f64,
    partition_id: &'a str,
    activity_id: &'a str,
}

#[derive(Clone, Default)]
pub struct RuTracker {
    inner: Arc<RuTrackerInner>,
}

#[derive(Default)]
struct RuTrackerInner {
    recent_activity: Mutex<VecDeque<ActivityEntry>>,
    container_ru: Mutex<HashMap<String, f64>>,
    total_requests: AtomicU64,
    total_ru: Mutex<f64>,
}

impl RuTracker {
    const MAX_RECENT_ACTIVITY: usize = 200;

    pub fn record_request(&self, entry: ActivityEntry) {
        self.inner.total_requests.fetch_add(1, Ordering::Relaxed);
        {
            let mut total_ru = self.inner.total_ru.lock().expect("RU total lock poisoned");
            *total_ru += entry.request_charge;
        }
        if let (Some(database_id), Some(container_id)) = (&entry.database_id, &entry.container_id) {
            let key = format!("{database_id}/{container_id}").to_ascii_lowercase();
            let mut container_ru = self
                .inner
                .container_ru
                .lock()
                .expect("container RU lock poisoned");
            *container_ru.entry(key).or_insert(0.0) += entry.request_charge;
        }
        let mut recent = self
            .inner
            .recent_activity
            .lock()
            .expect("activity lock poisoned");
        recent.push_back(entry);
        while recent.len() > Self::MAX_RECENT_ACTIVITY {
            recent.pop_front();
        }
    }
}

fn should_track(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    !lower.is_empty()
        && lower != "/"
        && lower != "/api/emulator/activity"
        && lower != "/api/emulator/explain"
        && lower != "/api/emulator/kql"
        && !lower.starts_with("/api/emulator/throughput")
        && !lower.starts_with("/explorer")
        && !lower.starts_with("/health")
        && !lower.starts_with("/swagger")
}

fn parse_request_charge(headers: &HeaderMap) -> f64 {
    headers
        .get(h::REQUEST_CHARGE)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<f64>().ok())
        .unwrap_or(1.0)
}

fn ensure_activity_id(headers: &mut HeaderMap) -> String {
    if let Some(value) = headers
        .get(h::ACTIVITY_ID)
        .and_then(|value| value.to_str().ok())
        .filter(|value| !value.trim().is_empty())
    {
        return value.to_string();
    }
    let activity_id = uuid::Uuid::new_v4().to_string();
    insert_header(headers, h::ACTIVITY_ID, activity_id.clone());
    activity_id
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
