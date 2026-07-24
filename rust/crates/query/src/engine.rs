//! The `QueryEngine` implementation backing the NoSQL `/docs` query endpoint.

use std::collections::HashMap;
use std::sync::Arc;

use async_trait::async_trait;
use cosmos_core::error::CosmosError;
use cosmos_core::models::feed::FeedResponse;
use cosmos_core::models::resources::{CosmosContainer, CosmosDocument, JsonObject};
use cosmos_core::models::vector::{VectorDistanceFunction, VectorSearchRequest};
use cosmos_core::traits::{DocumentStore, QueryEngine, QueryOptions, VectorIndexProvider};
use cosmos_core::CosmosResult;
use serde_json::Value;

use crate::ast::{Expr, SelectStmt};
use crate::eval::execute_with_udf_resolver;
use crate::parser::parse;
use crate::services::QueryExecutionLimiter;
use crate::udf::UdfResolver;

/// Hand-rolled Cosmos SQL engine.
///
/// Materializes the container (via `DocumentStore::list_documents`), then
/// filters/projects/sorts in memory. This mirrors the .NET engine's
/// whole-container materialization; streaming/bounded execution is a future
/// refinement (see the crate roadmap).
pub struct SqlQueryEngine {
    store: Arc<dyn DocumentStore>,
    limiter: QueryExecutionLimiter,
    vector_index: Option<Arc<dyn VectorIndexProvider>>,
    udf_resolver: Option<Arc<dyn UdfResolver>>,
}

impl SqlQueryEngine {
    pub fn new(store: Arc<dyn DocumentStore>) -> Self {
        Self {
            store,
            limiter: QueryExecutionLimiter::new_default(),
            vector_index: None,
            udf_resolver: None,
        }
    }

    pub fn with_limiter(store: Arc<dyn DocumentStore>, limiter: QueryExecutionLimiter) -> Self {
        Self {
            store,
            limiter,
            vector_index: None,
            udf_resolver: None,
        }
    }

    pub fn with_vector_index(
        store: Arc<dyn DocumentStore>,
        vector_index: Arc<dyn VectorIndexProvider>,
    ) -> Self {
        Self {
            store,
            limiter: QueryExecutionLimiter::new_default(),
            vector_index: Some(vector_index),
            udf_resolver: None,
        }
    }

    pub fn with_udf_resolver(
        store: Arc<dyn DocumentStore>,
        udf_resolver: Arc<dyn UdfResolver>,
    ) -> Self {
        Self {
            store,
            limiter: QueryExecutionLimiter::new_default(),
            vector_index: None,
            udf_resolver: Some(udf_resolver),
        }
    }

    pub fn with_vector_index_and_udf_resolver(
        store: Arc<dyn DocumentStore>,
        vector_index: Arc<dyn VectorIndexProvider>,
        udf_resolver: Arc<dyn UdfResolver>,
    ) -> Self {
        Self {
            store,
            limiter: QueryExecutionLimiter::new_default(),
            vector_index: Some(vector_index),
            udf_resolver: Some(udf_resolver),
        }
    }

    /// Overrides the query-execution concurrency limiter, consuming and
    /// returning `self` for builder-style chaining. Used by the host to honor
    /// `--max-concurrent-queries`.
    pub fn with_query_limiter(mut self, limiter: QueryExecutionLimiter) -> Self {
        self.limiter = limiter;
        self
    }

    async fn try_vector_order_by_docs(
        &self,
        database_id: &str,
        container_id: &str,
        stmt: &SelectStmt,
        parameters: Option<&HashMap<String, Value>>,
        options: Option<&QueryOptions>,
    ) -> CosmosResult<Option<FeedResponse<CosmosDocument>>> {
        let Some(index) = &self.vector_index else {
            return Ok(None);
        };
        if !index.is_enabled()
            || !stmt.joins.is_empty()
            || stmt.from_in.is_some()
            || !stmt.group_by.is_empty()
            || stmt.where_clause.is_some()
            || stmt.distinct
            || stmt.order_by.len() != 1
        {
            return Ok(None);
        }
        let (order_expr, desc) = &stmt.order_by[0];
        if *desc {
            return Ok(None);
        }
        let Expr::Call { name, args } = order_expr else {
            return Ok(None);
        };
        if !name.eq_ignore_ascii_case("VectorDistance") || args.len() < 2 {
            return Ok(None);
        }
        if matches!(args.get(2), Some(Expr::Lit(Value::Bool(true)))) {
            return Ok(None);
        }
        let Some(path) = path_to_index_path(&args[0]) else {
            return Ok(None);
        };
        let Some(query_vector) = const_vector(&args[1], parameters) else {
            return Ok(None);
        };
        let top = const_usize(stmt.top.as_ref(), parameters);
        let limit = const_usize(stmt.limit.as_ref(), parameters);
        let offset = const_usize(stmt.offset.as_ref(), parameters).unwrap_or(0);
        let Some(bound) = top.or_else(|| limit.map(|l| l + offset)) else {
            return Ok(None);
        };
        if bound == 0 {
            return Ok(Some(FeedResponse::new(Vec::new())));
        }

        let container = self.store.get_container(database_id, container_id).await?;
        let distance_function = vector_metric(args, &container);
        let index_type = container
            .indexing_policy
            .vector_indexes
            .as_ref()
            .and_then(|indexes| {
                indexes
                    .iter()
                    .find(|i| normalize_path(&i.path) == normalize_path(&path))
                    .map(|i| i.index_type.clone())
            })
            .unwrap_or_else(|| "quantizedFlat".to_string());
        if !index
            .ensure_index(
                database_id,
                container_id,
                &path,
                &index_type,
                distance_function,
            )
            .await?
        {
            return Ok(None);
        }

        let hits = index
            .search(VectorSearchRequest {
                database_id: database_id.to_string(),
                container_id: container_id.to_string(),
                path,
                query_vector,
                distance_function,
                top_k: bound,
                partition_key: options.and_then(|o| o.partition_key.clone()),
                index_type,
            })
            .await?;
        let mut docs = Vec::with_capacity(hits.len());
        for hit in hits {
            if let Ok(doc) = self
                .store
                .read_document(
                    database_id,
                    container_id,
                    &hit.document_id,
                    &hit.partition_key,
                )
                .await
            {
                if doc.is_indexed {
                    docs.push(doc);
                }
            }
        }
        Ok(Some(FeedResponse::new(docs)))
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

        // The engine intentionally materializes the container before applying
        // predicates/projection; keep peak memory bounded under concurrent load.
        let _permit = self.limiter.acquire().await;

        let feed = if let Some(vector_docs) = self
            .try_vector_order_by_docs(
                database_id,
                container_id,
                &stmt,
                parameters,
                options.as_ref(),
            )
            .await?
        {
            vector_docs
        } else {
            self.store.list_documents(database_id, container_id).await?
        };
        let docs: Vec<Value> = feed
            .resources
            .iter()
            .map(|d| Value::Object(d.to_response_body()))
            .collect();

        let empty = HashMap::new();
        let params = parameters.unwrap_or(&empty);
        let result = execute_with_udf_resolver(
            &stmt,
            &docs,
            params,
            Some(database_id),
            Some(container_id),
            self.udf_resolver.as_deref(),
        )
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

fn path_to_index_path(expr: &Expr) -> Option<String> {
    fn collect(expr: &Expr, parts: &mut Vec<String>) -> Option<()> {
        match expr {
            Expr::Identifier(_) => Some(()),
            Expr::Member(base, name) => {
                collect(base, parts)?;
                parts.push(name.clone());
                Some(())
            }
            _ => None,
        }
    }
    let mut parts = Vec::new();
    collect(expr, &mut parts)?;
    if parts.is_empty() {
        None
    } else {
        Some(format!("/{}", parts.join("/")))
    }
}

fn const_vector(expr: &Expr, parameters: Option<&HashMap<String, Value>>) -> Option<Vec<f32>> {
    match expr {
        Expr::Array(items) => items
            .iter()
            .map(|item| match item {
                Expr::Lit(Value::Number(n)) => n.as_f64().map(|v| v as f32),
                _ => None,
            })
            .collect(),
        Expr::Param(name) => parameters
            .and_then(|p| p.get(name))
            .and_then(Value::as_array)
            .and_then(|items| items.iter().map(|v| v.as_f64().map(|n| n as f32)).collect()),
        _ => None,
    }
}

fn const_usize(expr: Option<&Expr>, parameters: Option<&HashMap<String, Value>>) -> Option<usize> {
    match expr? {
        Expr::Lit(Value::Number(n)) => n.as_u64().map(|n| n as usize),
        Expr::Param(name) => parameters
            .and_then(|p| p.get(name))
            .and_then(Value::as_u64)
            .map(|n| n as usize),
        _ => None,
    }
}

fn vector_metric(args: &[Expr], container: &CosmosContainer) -> VectorDistanceFunction {
    if let Some(Expr::Object(fields)) = args.get(3) {
        if let Some((_, Expr::Lit(Value::String(df)))) = fields
            .iter()
            .find(|(key, _)| key.eq_ignore_ascii_case("distanceFunction"))
        {
            return VectorDistanceFunction::parse(Some(df));
        }
    }
    let Some(path) = path_to_index_path(&args[0]) else {
        return VectorDistanceFunction::default();
    };
    container
        .vector_embedding_policy
        .as_ref()
        .and_then(|policy| {
            policy
                .vector_embeddings
                .iter()
                .find(|embedding| normalize_path(&embedding.path) == normalize_path(&path))
                .or_else(|| policy.vector_embeddings.first())
        })
        .map(|embedding| VectorDistanceFunction::parse(Some(&embedding.distance_function)))
        .unwrap_or_default()
}

fn normalize_path(path: &str) -> String {
    format!("/{}", path.trim().trim_start_matches('/'))
}
