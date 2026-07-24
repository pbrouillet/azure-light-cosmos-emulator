//! Emulator information/admin endpoints under `/api/emulator`. Ports
//! `EmulatorInfoController`.

use std::sync::Arc;

use async_trait::async_trait;
use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use cosmos_core::error::{CosmosError, CosmosResult};
use cosmos_core::models::JsonObject;
use cosmos_core::traits::{ConsistencyManager as _, DocumentStore, EmulatorInfoService};
use cosmos_core::ConsistencyLevel;
use cosmos_query::QueryExplainService;
use serde_json::{json, Map, Value};

use crate::response::{json_response, parse_body_object, ApiError, HeaderOptions};
use crate::state::{AddressEndpoint, AppState, EmulatorSettings};

const EMULATOR_NAME: &str = "Azure Cosmos DB Light Emulator";
const DEFAULT_MASTER_KEY: &str = cosmos_auth::DEFAULT_MASTER_KEY;

pub async fn info(State(state): State<AppState>) -> Result<Response, ApiError> {
    let info = service_for_state(&state).get_info().await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        Value::Object(info),
    )
    .await)
}

pub async fn stats(State(state): State<AppState>) -> Result<Response, ApiError> {
    let stats = service_for_state(&state).get_stats().await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        Value::Object(stats),
    )
    .await)
}

pub async fn explain(State(state): State<AppState>, body: Bytes) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let database_id = obj.get("databaseId").and_then(Value::as_str);
    let container_id = obj.get("containerId").and_then(Value::as_str);
    let query = obj.get("query").and_then(Value::as_str);

    let mut errors = Map::new();
    if database_id.is_none_or(|value| value.trim().is_empty()) {
        errors.insert(
            "DatabaseId".into(),
            json!(["The databaseId field is required."]),
        );
    }
    if container_id.is_none_or(|value| value.trim().is_empty()) {
        errors.insert(
            "ContainerId".into(),
            json!(["The containerId field is required."]),
        );
    }
    if query.is_none_or(|value| value.trim().is_empty()) {
        errors.insert("Query".into(), json!(["The query field is required."]));
    }
    if !errors.is_empty() {
        return Ok(validation_problem(errors));
    }

    let database_id = database_id.expect("validated databaseId").trim();
    let container_id = container_id.expect("validated containerId").trim();
    let query = query
        .expect("validated query")
        .trim()
        .trim_end_matches(';')
        .trim();
    let container = state.store.get_container(database_id, container_id).await?;
    let explanation = QueryExplainService::explain(query).map_err(ApiError::bad_request)?;
    let result = explain_result(query, &container, explanation);
    let request_charge = result
        .get("estimatedRuCharge")
        .and_then(|v| v.get("total"))
        .and_then(Value::as_f64)
        .unwrap_or(1.0);

    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions {
            request_charge,
            database_id: Some(database_id.to_string()),
            container_id: Some(container_id.to_string()),
            ..Default::default()
        },
        result,
    )
    .await)
}

pub async fn update_settings(
    State(state): State<AppState>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let Some(enable_entra_id) = obj.get("enableEntraId").and_then(Value::as_bool) else {
        let mut errors = Map::new();
        errors.insert(
            "EnableEntraId".into(),
            json!(["The enableEntraId field is required."]),
        );
        return Ok(validation_problem(errors));
    };

    let tenant_id = obj.get("tenantId").and_then(Value::as_str);
    let client_id = obj.get("clientId").and_then(Value::as_str);
    let info = service_for_state(&state)
        .update_settings(enable_entra_id, tenant_id, client_id)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        Value::Object(info),
    )
    .await)
}

pub async fn get_database_throughput(
    State(state): State<AppState>,
    Path(db_id): Path<String>,
) -> Result<Response, ApiError> {
    let database = state.store.get_database(&db_id).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({ "id": database.id, "maxThroughput": database.max_throughput }),
    )
    .await)
}

pub async fn update_database_throughput(
    State(state): State<AppState>,
    Path(db_id): Path<String>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let obj = require_object_body(&body)?;
    let mut database = state.store.get_database(&db_id).await?;
    database.max_throughput = obj
        .get("maxThroughput")
        .and_then(Value::as_i64)
        .map(|value| value as i32);
    let updated = state.store.replace_database(database).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({ "id": updated.id, "maxThroughput": updated.max_throughput }),
    )
    .await)
}

pub async fn get_container_throughput(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let container = state.store.get_container(&db_id, &coll_id).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({ "id": container.id, "databaseId": db_id, "maxThroughput": container.max_throughput }),
    )
    .await)
}

pub async fn update_container_throughput(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let obj = require_object_body(&body)?;
    let mut container = state.store.get_container(&db_id, &coll_id).await?;
    container.max_throughput = obj
        .get("maxThroughput")
        .and_then(Value::as_i64)
        .map(|value| value as i32)
        .unwrap_or(400);
    let updated = state.store.replace_container(&db_id, container).await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        HeaderOptions::charge(1.0),
        json!({ "id": updated.id, "databaseId": db_id, "maxThroughput": updated.max_throughput }),
    )
    .await)
}

fn require_object_body(body: &[u8]) -> Result<JsonObject, ApiError> {
    if body.is_empty() {
        return Err(ApiError::bad_request("Request body is required."));
    }
    parse_body_object(body).map(|(obj, _)| obj)
}

fn service_for_state(state: &AppState) -> Arc<dyn EmulatorInfoService> {
    state.emulator_info.clone().unwrap_or_else(|| {
        Arc::new(DefaultEmulatorInfoService {
            store: state.store.clone(),
            endpoint: state.address_endpoint.clone(),
            default_consistency: state.consistency.default_consistency_level(),
            settings: state.emulator_settings.clone(),
        })
    })
}

fn explain_result(
    query: &str,
    container: &cosmos_core::models::CosmosContainer,
    explanation: Value,
) -> Value {
    let estimated_ru = explanation
        .get("estimatedCost")
        .and_then(|value| value.get("ru"))
        .and_then(Value::as_f64)
        .unwrap_or(1.0);
    json!({
        "query": query,
        "queryPlan": explanation.get("queryPlan").cloned().unwrap_or_else(|| json!({})),
        "estimatedRuCharge": {
            "base": estimated_ru,
            "filterCost": 0.0,
            "joinCost": 0.0,
            "aggregateCost": 0.0,
            "orderByCost": 0.0,
            "crossPartitionMultiplier": 1.0,
            "total": estimated_ru,
        },
        "indexAnalysis": {
            "usedIndexes": [],
            "recommendations": explanation.get("recommendations").cloned().unwrap_or_else(|| json!([])),
            "indexingPolicyPaths": {
                "included": container.indexing_policy.included_paths.iter().map(|path| path.path.clone()).collect::<Vec<_>>(),
                "excluded": container.indexing_policy.excluded_paths.iter().map(|path| path.path.clone()).collect::<Vec<_>>(),
            },
        },
        "warnings": explanation.get("warnings").cloned().unwrap_or_else(|| json!([])),
        "educationalNotes": explanation.get("notes").cloned().unwrap_or_else(|| json!([])),
    })
}

fn validation_problem(errors: Map<String, Value>) -> Response {
    (
        StatusCode::BAD_REQUEST,
        axum::Json(json!({
            "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            "title": "One or more validation errors occurred.",
            "status": 400,
            "errors": errors,
        })),
    )
        .into_response()
}

#[derive(Clone)]
struct DefaultEmulatorInfoService {
    store: Arc<dyn DocumentStore>,
    endpoint: AddressEndpoint,
    default_consistency: ConsistencyLevel,
    settings: Arc<std::sync::Mutex<EmulatorSettings>>,
}

#[async_trait]
impl EmulatorInfoService for DefaultEmulatorInfoService {
    async fn get_info(&self) -> CosmosResult<JsonObject> {
        let endpoint = self.no_sql_endpoint();
        let settings = self
            .settings
            .lock()
            .map_err(|_| CosmosError::internal_server_error("Emulator settings lock poisoned."))?
            .clone();

        let mut endpoints = Map::new();
        endpoints.insert("noSql".into(), Value::String(endpoint.clone()));
        endpoints.insert(
            "mongoDb".into(),
            Value::String("mongodb://localhost:10255".into()),
        );
        endpoints.insert(
            "explorer".into(),
            Value::String(format!("{endpoint}/explorer")),
        );

        let mut configuration = Map::new();
        configuration.insert("port".into(), json!(self.endpoint.port));
        configuration.insert("mongoPort".into(), json!(10255));
        configuration.insert("storage".into(), Value::String("Sqlite".into()));
        configuration.insert("dataDirectory".into(), Value::Null);
        configuration.insert(
            "consistencyLevel".into(),
            Value::String(consistency_name(self.default_consistency).into()),
        );
        configuration.insert("enableSsl".into(), json!(self.endpoint.enable_ssl));
        configuration.insert("enableExplorer".into(), json!(true));
        configuration.insert("enableEntraId".into(), json!(settings.enable_entra_id));
        configuration.insert(
            "tenantId".into(),
            settings.tenant_id.map(Value::String).unwrap_or(Value::Null),
        );
        configuration.insert(
            "clientId".into(),
            settings.client_id.map(Value::String).unwrap_or(Value::Null),
        );

        let mut result = Map::new();
        result.insert("name".into(), Value::String(EMULATOR_NAME.into()));
        result.insert(
            "version".into(),
            Value::String(env!("CARGO_PKG_VERSION").into()),
        );
        result.insert("endpoints".into(), Value::Object(endpoints));
        result.insert(
            "connectionString".into(),
            Value::String(format!(
                "AccountEndpoint={endpoint};AccountKey={DEFAULT_MASTER_KEY};"
            )),
        );
        result.insert("masterKey".into(), Value::String(DEFAULT_MASTER_KEY.into()));
        result.insert("configuration".into(), Value::Object(configuration));
        Ok(result)
    }

    async fn get_stats(&self) -> CosmosResult<JsonObject> {
        let databases = self.store.list_databases().await?;
        let mut container_count = 0usize;
        for database in &databases.resources {
            container_count += self
                .store
                .list_containers(&database.id)
                .await?
                .resources
                .len();
        }

        let mut result = Map::new();
        result.insert("totalRequestUnits".into(), json!(0.0));
        result.insert("totalRequests".into(), json!(0));
        result.insert("databaseCount".into(), json!(databases.resources.len()));
        result.insert("containerCount".into(), json!(container_count));
        result.insert("documentCount".into(), json!(0));
        result.insert("dataDirectory".into(), Value::Null);
        result.insert("dataSizeBytes".into(), json!(0));
        result.insert("uptimeSeconds".into(), json!(0));
        Ok(result)
    }

    async fn update_settings(
        &self,
        enable_entra_id: bool,
        tenant_id: Option<&str>,
        client_id: Option<&str>,
    ) -> CosmosResult<JsonObject> {
        {
            let mut settings = self.settings.lock().map_err(|_| {
                CosmosError::internal_server_error("Emulator settings lock poisoned.")
            })?;
            settings.enable_entra_id = enable_entra_id;
            settings.tenant_id = normalize(tenant_id);
            settings.client_id = normalize(client_id);
        }
        self.get_info().await
    }
}

impl DefaultEmulatorInfoService {
    fn no_sql_endpoint(&self) -> String {
        let scheme = if self.endpoint.enable_ssl {
            "https"
        } else {
            "http"
        };
        format!("{scheme}://localhost:{}", self.endpoint.port)
    }
}

fn normalize(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_string)
}

fn consistency_name(consistency: ConsistencyLevel) -> &'static str {
    match consistency {
        ConsistencyLevel::Strong => "Strong",
        ConsistencyLevel::BoundedStaleness => "BoundedStaleness",
        ConsistencyLevel::Session => "Session",
        ConsistencyLevel::ConsistentPrefix => "ConsistentPrefix",
        ConsistencyLevel::Eventual => "Eventual",
    }
}
