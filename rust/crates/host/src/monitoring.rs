use std::sync::Arc;

use axum::extract::{Query, State};
use axum::http::{HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router};
use cosmos_core::models::{headers as h, ActivityEntry, QueryTelemetryEntry};
use cosmos_core::traits::{ActivityStore, QueryTelemetryStore};
use cosmos_kql::{
    KqlColumnSchema, KqlError, KqlQueryExecutor, KqlResult, KqlSchemaRegistry, KqlTableSchema, Row,
};
use serde::Deserialize;
use serde_json::{json, Map, Value};

#[derive(Clone)]
pub struct MonitoringState {
    activity_store: Arc<dyn ActivityStore>,
    telemetry_store: Arc<dyn QueryTelemetryStore>,
    kql_executor: KqlQueryExecutor,
}

pub fn router(
    activity_store: Arc<dyn ActivityStore>,
    telemetry_store: Arc<dyn QueryTelemetryStore>,
) -> Router {
    let registry = KqlSchemaRegistry::default();
    register_monitoring_tables(&registry);
    let state = MonitoringState {
        activity_store,
        telemetry_store,
        kql_executor: KqlQueryExecutor::new(registry),
    };

    Router::new()
        .route("/api/emulator/activity", get(get_activity))
        .route(
            "/api/emulator/telemetry",
            get(get_telemetry).delete(clear_telemetry),
        )
        .route("/api/emulator/kql", post(execute_kql))
        .with_state(state)
}

async fn get_activity(State(state): State<MonitoringState>) -> Response {
    match state.activity_store.list(200).await {
        Ok(entries) => ok(json!(entries)),
        Err(error) => internal_error(error.message),
    }
}

#[derive(Deserialize)]
struct TelemetryQuery {
    db: Option<String>,
    container: Option<String>,
    max: Option<i32>,
}

async fn get_telemetry(
    State(state): State<MonitoringState>,
    Query(query): Query<TelemetryQuery>,
) -> Response {
    match state
        .telemetry_store
        .list(
            query.db.as_deref(),
            query.container.as_deref(),
            query.max.unwrap_or(100),
        )
        .await
    {
        Ok(entries) => ok(json!(entries)),
        Err(error) => internal_error(error.message),
    }
}

async fn clear_telemetry(State(state): State<MonitoringState>) -> Response {
    match state.telemetry_store.clear().await {
        Ok(()) => (StatusCode::NO_CONTENT, common_headers()).into_response(),
        Err(error) => internal_error(error.message),
    }
}

#[derive(Deserialize)]
struct KqlRequest {
    query: String,
}

async fn execute_kql(
    State(state): State<MonitoringState>,
    Json(request): Json<KqlRequest>,
) -> Response {
    if request.query.trim().is_empty() {
        return bad_request("BadRequest", "Query text is required.", None);
    }

    let adapter =
        match MonitoringStorageAdapter::load(state.activity_store, state.telemetry_store).await {
            Ok(adapter) => adapter,
            Err(error) => return internal_error(error.to_string()),
        };
    match state
        .kql_executor
        .execute(&request.query, |table| adapter.resolve_table(table))
    {
        Ok(result) => {
            let columns: Vec<Value> = result
                .schema
                .columns
                .iter()
                .map(|column| json!({ "name": column.name, "type": column.kql_type }))
                .collect();
            let rows: Vec<Value> = result
                .rows
                .iter()
                .map(|row| {
                    Value::Array(
                        result
                            .schema
                            .columns
                            .iter()
                            .map(|column| row.get(&column.name).cloned().unwrap_or(Value::Null))
                            .collect(),
                    )
                })
                .collect();
            ok(json!({ "columns": columns, "rows": rows }))
        }
        Err(KqlError::UnsupportedOperator(message)) => {
            bad_request("UnsupportedOperation", &message, None)
        }
        Err(error) => bad_request(
            "KqlParseError",
            &error.to_string(),
            Some(Value::Array(vec![])),
        ),
    }
}

struct MonitoringStorageAdapter {
    activity_rows: Vec<Row>,
    telemetry_rows: Vec<Row>,
}

impl MonitoringStorageAdapter {
    async fn load(
        activity_store: Arc<dyn ActivityStore>,
        telemetry_store: Arc<dyn QueryTelemetryStore>,
    ) -> KqlResult<Self> {
        let activity_entries = activity_store.list(10_000).await.map_err(|error| {
            KqlError::Other(anyhow::anyhow!(
                "failed to read activity: {}",
                error.message
            ))
        })?;
        let telemetry_entries =
            telemetry_store
                .list(None, None, 10_000)
                .await
                .map_err(|error| {
                    KqlError::Other(anyhow::anyhow!(
                        "failed to read telemetry: {}",
                        error.message
                    ))
                })?;
        Ok(Self {
            activity_rows: activity_entries.into_iter().map(activity_row).collect(),
            telemetry_rows: telemetry_entries.into_iter().map(telemetry_row).collect(),
        })
    }

    fn resolve_table(&self, table_name: &str) -> KqlResult<Vec<Row>> {
        match table_name.to_ascii_lowercase().as_str() {
            "activity" => Ok(self.activity_rows.clone()),
            "telemetry" => Ok(self.telemetry_rows.clone()),
            _ => Err(KqlError::Other(anyhow::anyhow!(
                "Unknown table '{table_name}'. Available tables: activity, telemetry"
            ))),
        }
    }
}

fn activity_row(entry: ActivityEntry) -> Row {
    let mut row = Map::new();
    row.insert("timestamp".into(), json!(entry.timestamp));
    row.insert("method".into(), json!(entry.method));
    row.insert("path".into(), json!(entry.path));
    row.insert("statusCode".into(), json!(i64::from(entry.status_code)));
    row.insert("requestCharge".into(), json!(entry.request_charge));
    row.insert("latencyMs".into(), json!(entry.latency_ms));
    row.insert("databaseId".into(), option_string(entry.database_id));
    row.insert("containerId".into(), option_string(entry.container_id));
    row
}

fn telemetry_row(entry: QueryTelemetryEntry) -> Row {
    let mut row = Map::new();
    row.insert("timestamp".into(), json!(entry.timestamp));
    row.insert("databaseId".into(), json!(entry.database_id));
    row.insert("containerId".into(), json!(entry.container_id));
    row.insert("sqlText".into(), json!(entry.sql_text));
    row.insert("partitionKey".into(), option_string(entry.partition_key));
    row.insert("consistencyLevel".into(), json!(entry.consistency_level));
    row.insert("requestCharge".into(), json!(entry.request_charge));
    row.insert("latencyMs".into(), json!(entry.latency_ms));
    row.insert("itemCount".into(), json!(i64::from(entry.item_count)));
    row.insert("statusCode".into(), json!(i64::from(entry.status_code)));
    row.insert("activityId".into(), json!(entry.activity_id));
    row.insert("isCrossPartition".into(), json!(entry.is_cross_partition));
    row
}

fn option_string(value: Option<String>) -> Value {
    value.map(Value::String).unwrap_or(Value::Null)
}

fn register_monitoring_tables(registry: &KqlSchemaRegistry) {
    registry.register_table(KqlTableSchema::new(
        "activity",
        vec![
            KqlColumnSchema::new("timestamp", "datetime"),
            KqlColumnSchema::new("method", "string"),
            KqlColumnSchema::new("path", "string"),
            KqlColumnSchema::new("statusCode", "long"),
            KqlColumnSchema::new("requestCharge", "real"),
            KqlColumnSchema::new("latencyMs", "real"),
            KqlColumnSchema::new("databaseId", "string"),
            KqlColumnSchema::new("containerId", "string"),
        ],
    ));
    registry.register_table(KqlTableSchema::new(
        "telemetry",
        vec![
            KqlColumnSchema::new("timestamp", "datetime"),
            KqlColumnSchema::new("databaseId", "string"),
            KqlColumnSchema::new("containerId", "string"),
            KqlColumnSchema::new("sqlText", "string"),
            KqlColumnSchema::new("partitionKey", "string"),
            KqlColumnSchema::new("consistencyLevel", "string"),
            KqlColumnSchema::new("requestCharge", "real"),
            KqlColumnSchema::new("latencyMs", "long"),
            KqlColumnSchema::new("itemCount", "long"),
            KqlColumnSchema::new("statusCode", "long"),
            KqlColumnSchema::new("activityId", "string"),
            KqlColumnSchema::new("isCrossPartition", "bool"),
        ],
    ));
}

fn ok(body: Value) -> Response {
    (StatusCode::OK, common_headers(), Json(body)).into_response()
}

fn internal_error(message: String) -> Response {
    let body = json!({ "code": "InternalServerError", "message": message });
    (
        StatusCode::INTERNAL_SERVER_ERROR,
        common_headers(),
        Json(body),
    )
        .into_response()
}

fn bad_request(code: &str, message: &str, diagnostics: Option<Value>) -> Response {
    let mut body = Map::new();
    body.insert("code".into(), Value::String(code.to_string()));
    body.insert("message".into(), Value::String(message.to_string()));
    if let Some(diagnostics) = diagnostics {
        body.insert("diagnostics".into(), diagnostics);
    }
    (
        StatusCode::BAD_REQUEST,
        common_headers(),
        Json(Value::Object(body)),
    )
        .into_response()
}

fn common_headers() -> HeaderMap {
    let mut headers = HeaderMap::new();
    insert_header(&mut headers, h::REQUEST_CHARGE, "1.00");
    insert_header(
        &mut headers,
        h::ACTIVITY_ID,
        uuid::Uuid::new_v4().to_string(),
    );
    insert_header(&mut headers, h::SERVICE_VERSION, h::CURRENT_SERVICE_VERSION);
    headers
}

fn insert_header(headers: &mut HeaderMap, name: &'static str, value: impl ToString) {
    if let Ok(value) = HeaderValue::from_str(&value.to_string()) {
        headers.insert(name, value);
    }
}
