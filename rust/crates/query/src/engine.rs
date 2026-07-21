//! The `QueryEngine` implementation backing the NoSQL `/docs` query endpoint.

use std::collections::HashMap;
use std::sync::Arc;

use async_trait::async_trait;
use cosmos_core::error::CosmosError;
use cosmos_core::models::feed::FeedResponse;
use cosmos_core::models::resources::JsonObject;
use cosmos_core::traits::{DocumentStore, QueryEngine, QueryOptions};
use cosmos_core::CosmosResult;
use serde_json::Value;

use crate::eval::execute;
use crate::parser::parse;

/// Hand-rolled Cosmos SQL engine.
///
/// Materializes the container (via `DocumentStore::list_documents`), then
/// filters/projects/sorts in memory. This mirrors the .NET engine's
/// whole-container materialization; streaming/bounded execution is a future
/// refinement (see the crate roadmap).
pub struct SqlQueryEngine {
    store: Arc<dyn DocumentStore>,
}

impl SqlQueryEngine {
    pub fn new(store: Arc<dyn DocumentStore>) -> Self {
        Self { store }
    }
}

#[async_trait]
impl QueryEngine for SqlQueryEngine {
    async fn execute_query(
        &self,
        database_id: &str,
        container_id: &str,
        query: &str,
        parameters: Option<&HashMap<String, Value>>,
        options: Option<QueryOptions>,
    ) -> CosmosResult<FeedResponse<JsonObject>> {
        let stmt =
            parse(query).map_err(|e| CosmosError::bad_request(format!("Invalid query: {e}")))?;

        let feed = self.store.list_documents(database_id, container_id).await?;
        let docs: Vec<Value> = feed
            .resources
            .iter()
            .map(|d| Value::Object(d.to_response_body()))
            .collect();

        let empty = HashMap::new();
        let params = parameters.unwrap_or(&empty);
        let result = execute(&stmt, &docs, params)
            .map_err(|e| CosmosError::bad_request(format!("Query execution failed: {e}")))?;

        // Offset-based paging over the fully-computed result set.
        let mut rows = result.rows;
        let start: usize = options
            .as_ref()
            .and_then(|o| o.continuation_token.as_deref())
            .and_then(|t| t.parse().ok())
            .unwrap_or(0);
        let max = options
            .as_ref()
            .and_then(|o| o.max_item_count)
            .filter(|&n| n > 0)
            .map(|n| n as usize);

        let total = rows.len();
        if start > 0 {
            rows.drain(0..start.min(total));
        }
        let mut continuation = None;
        if let Some(max) = max {
            if rows.len() > max {
                rows.truncate(max);
                continuation = Some((start + max).to_string());
            }
        }

        let mut response = FeedResponse::new(rows);
        response.rid = container_id.to_string();
        response.is_value_projection = result.is_value;
        response.continuation_token = continuation;
        Ok(response)
    }
}
