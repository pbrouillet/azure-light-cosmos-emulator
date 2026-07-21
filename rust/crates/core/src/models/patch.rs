//! Patch operations for the PATCH document API. Ports `PatchOperation.cs`.

use serde_json::Value;

/// A single patch operation.
#[derive(Debug, Clone)]
pub struct PatchOperation {
    /// Operation: `add`, `set`, `replace`, `remove`, `incr`, `move`.
    pub op: String,
    /// Target path, e.g. `/address/city`.
    pub path: String,
    /// Value for add/set/replace/incr operations.
    pub value: Option<Value>,
    /// Source path for move operations.
    pub from: Option<String>,
}

/// A PATCH document request body.
#[derive(Debug, Clone)]
pub struct PatchRequest {
    pub operations: Vec<PatchOperation>,
    /// Optional SQL condition for conditional patching.
    pub condition: Option<String>,
}
