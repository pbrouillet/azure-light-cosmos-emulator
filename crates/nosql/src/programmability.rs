//! Stored procedure, trigger, and UDF endpoints.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::Response;
use cosmos_core::models::headers as h;
use cosmos_core::models::programmability::{
    StoredProcedure, Trigger, TriggerOperation, TriggerType, UserDefinedFunction,
};
use cosmos_core::traits::ProgrammabilityEngine;
use serde_json::{json, Value};

use crate::response::{
    empty_response, json_response, parse_body_object, parse_partition_key, ApiError, HeaderOptions,
};
use crate::state::AppState;

pub async fn create_sproc(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let id = required_string(&obj, "id")?;
    let body = required_string(&obj, "body")?;
    let sproc = StoredProcedure::new(&db_id, &coll_id, id, body);
    let result = state
        .programmability
        .create_stored_procedure(&db_id, &coll_id, sproc)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::CREATED,
        write_options(&db_id, &coll_id, 5.0),
        format_sproc(&result),
    )
    .await)
}

pub async fn list_sprocs(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let result = state
        .programmability
        .list_stored_procedures(&db_id, &coll_id)
        .await?;
    let items: Vec<Value> = result.resources.iter().map(format_sproc).collect();
    Ok(json_response(
        &state,
        StatusCode::OK,
        read_options(&db_id, &coll_id),
        json!({ "_rid": "", "StoredProcedures": items, "_count": result.count() }),
    )
    .await)
}

pub async fn get_sproc(
    State(state): State<AppState>,
    Path((db_id, coll_id, sproc_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    let result = state
        .programmability
        .get_stored_procedure(&db_id, &coll_id, &sproc_id)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        read_options(&db_id, &coll_id),
        format_sproc(&result),
    )
    .await)
}

pub async fn replace_sproc(
    State(state): State<AppState>,
    Path((db_id, coll_id, sproc_id)): Path<(String, String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let body = required_string(&obj, "body")?;
    let sproc = StoredProcedure::new(&db_id, &coll_id, sproc_id, body);
    let result = state
        .programmability
        .replace_stored_procedure(&db_id, &coll_id, sproc)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        write_options(&db_id, &coll_id, 1.0),
        format_sproc(&result),
    )
    .await)
}

pub async fn execute_sproc(
    State(state): State<AppState>,
    Path((db_id, coll_id, sproc_id)): Path<(String, String, String)>,
    headers: HeaderMap,
    body: Bytes,
) -> Result<Response, ApiError> {
    let args: Vec<Value> = match serde_json::from_slice::<Value>(&body) {
        Ok(Value::Array(items)) => items,
        Ok(_) => {
            return Err(ApiError::bad_request(
                "Stored procedure arguments must be an array.",
            ))
        }
        Err(_) => return Err(ApiError::bad_request("Request body must be valid JSON.")),
    };
    let pk = parse_partition_key(header_str(&headers, h::PARTITION_KEY).as_deref());
    let result = state
        .programmability
        .execute_stored_procedure(&db_id, &coll_id, &sproc_id, &args, &pk)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        write_options(&db_id, &coll_id, 5.0),
        result.unwrap_or(Value::Null),
    )
    .await)
}

pub async fn delete_sproc(
    State(state): State<AppState>,
    Path((db_id, coll_id, sproc_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    state
        .programmability
        .delete_stored_procedure(&db_id, &coll_id, &sproc_id)
        .await?;
    Ok(empty_response(
        &state,
        StatusCode::NO_CONTENT,
        write_options(&db_id, &coll_id, 5.0),
    )
    .await)
}

pub async fn create_trigger(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let id = required_string(&obj, "id")?;
    let body = required_string(&obj, "body")?;
    let trigger = Trigger::new(
        &db_id,
        &coll_id,
        id,
        body,
        parse_trigger_type(obj.get("triggerType")),
        parse_trigger_operation(obj.get("triggerOperation")),
    );
    let result = state
        .programmability
        .create_trigger(&db_id, &coll_id, trigger)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::CREATED,
        write_options(&db_id, &coll_id, 5.0),
        format_trigger(&result),
    )
    .await)
}

pub async fn list_triggers(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let result = state
        .programmability
        .list_triggers(&db_id, &coll_id)
        .await?;
    let items: Vec<Value> = result.resources.iter().map(format_trigger).collect();
    Ok(json_response(
        &state,
        StatusCode::OK,
        read_options(&db_id, &coll_id),
        json!({ "_rid": "", "Triggers": items, "_count": result.count() }),
    )
    .await)
}

pub async fn get_trigger(
    State(state): State<AppState>,
    Path((db_id, coll_id, trigger_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    let result = state
        .programmability
        .get_trigger(&db_id, &coll_id, &trigger_id)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        read_options(&db_id, &coll_id),
        format_trigger(&result),
    )
    .await)
}

pub async fn replace_trigger(
    State(state): State<AppState>,
    Path((db_id, coll_id, trigger_id)): Path<(String, String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let body = required_string(&obj, "body")?;
    let trigger = Trigger::new(
        &db_id,
        &coll_id,
        trigger_id,
        body,
        parse_trigger_type(obj.get("triggerType")),
        parse_trigger_operation(obj.get("triggerOperation")),
    );
    let result = state
        .programmability
        .replace_trigger(&db_id, &coll_id, trigger)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        write_options(&db_id, &coll_id, 1.0),
        format_trigger(&result),
    )
    .await)
}

pub async fn delete_trigger(
    State(state): State<AppState>,
    Path((db_id, coll_id, trigger_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    state
        .programmability
        .delete_trigger(&db_id, &coll_id, &trigger_id)
        .await?;
    Ok(empty_response(
        &state,
        StatusCode::NO_CONTENT,
        write_options(&db_id, &coll_id, 5.0),
    )
    .await)
}

pub async fn create_udf(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let id = required_string(&obj, "id")?;
    let body = required_string(&obj, "body")?;
    let udf = UserDefinedFunction::new(&db_id, &coll_id, id, body);
    let result = state
        .programmability
        .create_udf(&db_id, &coll_id, udf)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::CREATED,
        write_options(&db_id, &coll_id, 5.0),
        format_udf(&result),
    )
    .await)
}

pub async fn list_udfs(
    State(state): State<AppState>,
    Path((db_id, coll_id)): Path<(String, String)>,
) -> Result<Response, ApiError> {
    let result = state.programmability.list_udfs(&db_id, &coll_id).await?;
    let items: Vec<Value> = result.resources.iter().map(format_udf).collect();
    Ok(json_response(
        &state,
        StatusCode::OK,
        read_options(&db_id, &coll_id),
        json!({ "_rid": "", "UserDefinedFunctions": items, "_count": result.count() }),
    )
    .await)
}

pub async fn get_udf(
    State(state): State<AppState>,
    Path((db_id, coll_id, udf_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    let result = state
        .programmability
        .get_udf(&db_id, &coll_id, &udf_id)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        read_options(&db_id, &coll_id),
        format_udf(&result),
    )
    .await)
}

pub async fn replace_udf(
    State(state): State<AppState>,
    Path((db_id, coll_id, udf_id)): Path<(String, String, String)>,
    body: Bytes,
) -> Result<Response, ApiError> {
    let (obj, _) = parse_body_object(&body)?;
    let body = required_string(&obj, "body")?;
    let udf = UserDefinedFunction::new(&db_id, &coll_id, udf_id, body);
    let result = state
        .programmability
        .replace_udf(&db_id, &coll_id, udf)
        .await?;
    Ok(json_response(
        &state,
        StatusCode::OK,
        write_options(&db_id, &coll_id, 1.0),
        format_udf(&result),
    )
    .await)
}

pub async fn delete_udf(
    State(state): State<AppState>,
    Path((db_id, coll_id, udf_id)): Path<(String, String, String)>,
) -> Result<Response, ApiError> {
    state
        .programmability
        .delete_udf(&db_id, &coll_id, &udf_id)
        .await?;
    Ok(empty_response(
        &state,
        StatusCode::NO_CONTENT,
        write_options(&db_id, &coll_id, 5.0),
    )
    .await)
}

fn required_string(obj: &serde_json::Map<String, Value>, field: &str) -> Result<String, ApiError> {
    obj.get(field)
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
        .ok_or_else(|| ApiError::bad_request(format!("Missing '{field}' property.")))
}

fn parse_trigger_type(value: Option<&Value>) -> TriggerType {
    match value
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_ascii_lowercase()
        .as_str()
    {
        "post" => TriggerType::Post,
        _ => TriggerType::Pre,
    }
}

fn parse_trigger_operation(value: Option<&Value>) -> TriggerOperation {
    match value
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_ascii_lowercase()
        .as_str()
    {
        "create" => TriggerOperation::Create,
        "replace" => TriggerOperation::Replace,
        "delete" => TriggerOperation::Delete,
        _ => TriggerOperation::All,
    }
}

fn trigger_type_name(value: TriggerType) -> &'static str {
    match value {
        TriggerType::Pre => "Pre",
        TriggerType::Post => "Post",
    }
}

fn trigger_operation_name(value: TriggerOperation) -> &'static str {
    match value {
        TriggerOperation::All => "All",
        TriggerOperation::Create => "Create",
        TriggerOperation::Replace => "Replace",
        TriggerOperation::Delete => "Delete",
    }
}

fn read_options(db_id: &str, coll_id: &str) -> HeaderOptions {
    HeaderOptions {
        request_charge: 1.0,
        database_id: Some(db_id.to_string()),
        container_id: Some(coll_id.to_string()),
        ..Default::default()
    }
}

fn write_options(db_id: &str, coll_id: &str, request_charge: f64) -> HeaderOptions {
    HeaderOptions {
        request_charge,
        database_id: Some(db_id.to_string()),
        container_id: Some(coll_id.to_string()),
        include_session_token: true,
        ..Default::default()
    }
}

fn header_str(headers: &HeaderMap, name: &str) -> Option<String> {
    headers
        .get(name)
        .and_then(|v| v.to_str().ok())
        .map(str::to_string)
}

fn format_sproc(s: &StoredProcedure) -> Value {
    json!({
        "id": s.id,
        "_rid": s.rid,
        "_self": s.self_link,
        "_etag": s.etag,
        "_ts": s.timestamp,
        "body": s.body
    })
}

fn format_trigger(t: &Trigger) -> Value {
    json!({
        "id": t.id,
        "_rid": t.rid,
        "_self": t.self_link,
        "_etag": t.etag,
        "_ts": t.timestamp,
        "body": t.body,
        "triggerType": trigger_type_name(t.trigger_type),
        "triggerOperation": trigger_operation_name(t.trigger_operation)
    })
}

fn format_udf(u: &UserDefinedFunction) -> Value {
    json!({
        "id": u.id,
        "_rid": u.rid,
        "_self": u.self_link,
        "_etag": u.etag,
        "_ts": u.timestamp,
        "body": u.body
    })
}
