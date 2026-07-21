//! Transactional batch operations. Ports `BatchOperation.cs`.

use crate::models::resources::JsonObject;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BatchOperationType {
    Create,
    Read,
    Replace,
    Upsert,
    Delete,
    Patch,
}

/// A single operation within a transactional batch request.
#[derive(Debug, Clone)]
pub struct BatchOperationRequest {
    pub operation_type: BatchOperationType,
    pub id: Option<String>,
    pub resource_body: Option<JsonObject>,
    pub if_match: Option<String>,
    pub if_none_match: Option<String>,
}

/// The result of a single operation within a transactional batch response.
#[derive(Debug, Clone)]
pub struct BatchOperationResponse {
    pub status_code: u16,
    pub resource_body: Option<JsonObject>,
    pub etag: Option<String>,
    pub request_charge: f64,
    pub retry_after_ms: Option<i32>,
}
