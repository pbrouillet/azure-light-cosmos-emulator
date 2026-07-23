//! Query telemetry models. Ports `QueryTelemetryEntry.cs`.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// A single query telemetry record capturing execution metadata.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QueryTelemetryEntry {
    pub id: String,
    pub timestamp: DateTime<Utc>,
    pub database_id: String,
    pub container_id: String,
    pub sql_text: String,
    pub partition_key: Option<String>,
    pub consistency_level: String,
    pub request_charge: f64,
    pub latency_ms: i64,
    pub item_count: i32,
    pub status_code: i32,
    pub activity_id: String,
    pub continuation_token: Option<String>,
    pub is_cross_partition: bool,
    pub query_plan: Option<String>,
}

impl Default for QueryTelemetryEntry {
    fn default() -> Self {
        Self {
            id: Uuid::new_v4().simple().to_string(),
            timestamp: Utc::now(),
            database_id: String::new(),
            container_id: String::new(),
            sql_text: String::new(),
            partition_key: None,
            consistency_level: String::new(),
            request_charge: 0.0,
            latency_ms: 0,
            item_count: 0,
            status_code: 0,
            activity_id: String::new(),
            continuation_token: None,
            is_cross_partition: false,
            query_plan: None,
        }
    }
}
