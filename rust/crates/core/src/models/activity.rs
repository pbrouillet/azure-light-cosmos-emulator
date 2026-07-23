//! HTTP request activity log model. Ports `ActivityEntry` from
//! `IActivityStore.cs`.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// A single HTTP request activity record for persistent storage.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActivityEntry {
    pub timestamp: DateTime<Utc>,
    pub method: String,
    pub path: String,
    pub status_code: i32,
    pub request_charge: f64,
    pub latency_ms: f64,
    pub database_id: Option<String>,
    pub container_id: Option<String>,
}
