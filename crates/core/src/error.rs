//! Error type mapping to Cosmos DB error responses. Ports
//! `CosmosEmulatorException` (`src/Core/Exceptions/CosmosEmulatorException.cs`).

use uuid::Uuid;

/// An error that maps to a Cosmos DB HTTP error response.
#[derive(Debug, Clone, thiserror::Error)]
#[error("{error_code} ({status_code}): {message}")]
pub struct CosmosError {
    /// HTTP status code (e.g. 404, 409).
    pub status_code: u16,
    /// Cosmos error code string (e.g. "NotFound", "Conflict").
    pub error_code: String,
    /// Human-readable message.
    pub message: String,
    /// Activity ID for request tracing.
    pub activity_id: String,
    /// Request charge in RUs.
    pub request_charge: f64,
    /// Suggested retry delay in milliseconds (for 429s).
    pub retry_after_ms: Option<u32>,
}

impl CosmosError {
    fn new(status_code: u16, error_code: &str, message: impl Into<String>) -> Self {
        Self {
            status_code,
            error_code: error_code.to_string(),
            message: message.into(),
            activity_id: Uuid::new_v4().to_string(),
            request_charge: 1.0,
            retry_after_ms: None,
        }
    }

    pub fn not_found(resource_type: &str, resource_id: &str) -> Self {
        Self::new(
            404,
            "NotFound",
            format!("Resource {resource_type} with id '{resource_id}' was not found."),
        )
    }

    pub fn conflict(resource_type: &str, resource_id: &str) -> Self {
        Self::new(
            409,
            "Conflict",
            format!("Resource {resource_type} with id '{resource_id}' already exists."),
        )
    }

    pub fn precondition_failed(message: impl Into<String>) -> Self {
        Self::new(412, "PreconditionFailed", message)
    }

    pub fn bad_request(message: impl Into<String>) -> Self {
        Self::new(400, "BadRequest", message)
    }

    pub fn unauthorized(message: impl Into<String>) -> Self {
        Self::new(401, "Unauthorized", message)
    }

    pub fn forbidden(message: impl Into<String>) -> Self {
        Self::new(403, "Forbidden", message)
    }

    pub fn method_not_allowed(message: impl Into<String>) -> Self {
        Self::new(405, "MethodNotAllowed", message)
    }

    pub fn too_many_requests(message: impl Into<String>, retry_after_ms: u32) -> Self {
        let mut err = Self::new(429, "TooManyRequests", message);
        err.request_charge = 0.0;
        err.retry_after_ms = Some(retry_after_ms);
        err
    }

    pub fn request_timeout(message: impl Into<String>) -> Self {
        Self::new(408, "RequestTimeout", message)
    }

    pub fn entity_too_large(message: impl Into<String>) -> Self {
        Self::new(413, "RequestEntityTooLarge", message)
    }

    pub fn internal_server_error(message: impl Into<String>) -> Self {
        Self::new(500, "InternalServerError", message)
    }

    pub fn gone(message: impl Into<String>) -> Self {
        Self::new(410, "Gone", message)
    }
}

/// Convenience result alias used throughout the emulator.
pub type CosmosResult<T> = Result<T, CosmosError>;
