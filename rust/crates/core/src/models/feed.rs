//! Feed (list) response. Ports `FeedResponse.cs`.

use uuid::Uuid;

/// A Cosmos DB feed (list) response wrapping a page of resources.
#[derive(Debug, Clone)]
pub struct FeedResponse<T> {
    /// Resource ID of the containing resource.
    pub rid: String,
    /// The returned resources.
    pub resources: Vec<T>,
    /// Continuation token for pagination.
    pub continuation_token: Option<String>,
    /// Request charge in RUs.
    pub request_charge: f64,
    /// Activity ID for request tracing.
    pub activity_id: String,
    /// Session token.
    pub session_token: Option<String>,
    /// RU cost multiplier applied when a scan was required.
    pub ru_multiplier: f64,
    /// True when the query used `SELECT VALUE` projection.
    pub is_value_projection: bool,
}

impl<T> FeedResponse<T> {
    pub fn new(resources: Vec<T>) -> Self {
        Self {
            rid: String::new(),
            resources,
            continuation_token: None,
            request_charge: 1.0,
            activity_id: Uuid::new_v4().to_string(),
            session_token: None,
            ru_multiplier: 1.0,
            is_value_projection: false,
        }
    }

    /// The number of items returned.
    pub fn count(&self) -> usize {
        self.resources.len()
    }
}

impl<T> Default for FeedResponse<T> {
    fn default() -> Self {
        Self::new(Vec::new())
    }
}
