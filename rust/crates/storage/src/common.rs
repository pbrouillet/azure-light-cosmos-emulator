//! Shared helpers used by all `DocumentStore` backends. Ports the relevant
//! parts of `src/Storage/DocumentStoreHelpers.cs`.

use cosmos_core::error::{CosmosError, CosmosResult};
use cosmos_core::models::{CosmosContainer, JsonObject, PartitionKeyValue};
use serde_json::Value;

/// Maximum document size in bytes (2 MB), matching the .NET emulator.
pub const MAX_DOCUMENT_SIZE_BYTES: usize = 2 * 1024 * 1024;

/// Extracts the partition key value from a document body using the container's
/// partition key path(s). Mirrors `DocumentStoreHelpers.ExtractPartitionKey`:
/// each path is looked up (supporting nested `/a/b` paths); numbers are kept as
/// JSON numbers, missing values become `null`.
pub fn extract_partition_key(container: &CosmosContainer, body: &JsonObject) -> PartitionKeyValue {
    let components: Vec<Value> = container
        .partition_key
        .paths
        .iter()
        .map(|path| lookup_path(body, path).unwrap_or(Value::Null))
        .collect();
    if components.is_empty() {
        PartitionKeyValue::undefined()
    } else {
        PartitionKeyValue::multi(components)
    }
}

/// Resolves a JSON pointer-like path (e.g. `/tenantId` or `/a/b`) in a body.
pub fn lookup_path(body: &JsonObject, path: &str) -> Option<Value> {
    let mut current = Value::Object(body.clone());
    for segment in path.trim_start_matches('/').split('/') {
        if segment.is_empty() {
            continue;
        }
        current = current.get(segment)?.clone();
    }
    Some(current)
}

/// Serializes partition key components to a canonical JSON array string, used as
/// the persisted `partition_key_json` column and as a lookup key.
pub fn serialize_partition_key(pk: &PartitionKeyValue) -> String {
    serde_json::to_string(&pk.components).unwrap_or_else(|_| "[]".to_string())
}

/// Deserializes a persisted `partition_key_json` array string.
pub fn deserialize_partition_key(json: &str) -> PartitionKeyValue {
    let components: Vec<Value> = serde_json::from_str(json).unwrap_or_default();
    PartitionKeyValue::multi(components)
}

/// Returns the required, non-empty `id` field from a document body.
pub fn require_id(body: &JsonObject) -> CosmosResult<String> {
    match body.get("id") {
        Some(Value::String(s)) if !s.is_empty() => Ok(s.clone()),
        _ => Err(CosmosError::bad_request(
            "Document must have a non-empty 'id'.",
        )),
    }
}

/// Reads an optional `ttl` field from a document body.
pub fn extract_ttl(body: &JsonObject) -> Option<i32> {
    body.get("ttl").and_then(|v| v.as_i64()).map(|v| v as i32)
}

/// Applies a single patch operation to a document body. Supports the common
/// operations (`add`/`set`/`replace`/`remove`/`incr`).
pub fn apply_patch(
    body: &mut JsonObject,
    op: &cosmos_core::models::PatchOperation,
) -> CosmosResult<()> {
    let field = op.path.trim_start_matches('/').replace('/', ".");
    match op.op.as_str() {
        "add" | "set" | "replace" => {
            let value = op
                .value
                .clone()
                .ok_or_else(|| CosmosError::bad_request("Patch op requires a value."))?;
            body.insert(field, value);
        }
        "remove" => {
            body.remove(&field);
        }
        "incr" => {
            let delta = op
                .value
                .as_ref()
                .and_then(|v| v.as_f64())
                .ok_or_else(|| CosmosError::bad_request("incr requires a numeric value."))?;
            let current = body.get(&field).and_then(|v| v.as_f64()).unwrap_or(0.0);
            body.insert(field, Value::from(current + delta));
        }
        other => {
            return Err(CosmosError::bad_request(format!(
                "Unsupported patch operation '{other}'."
            )))
        }
    }
    Ok(())
}
