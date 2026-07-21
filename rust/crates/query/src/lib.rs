//! Query engines for the Cosmos DB light emulator.
//!
//! Ports the .NET `NoSql/Query` (Cosmos SQL) and `Kql` projects. The SQL engine
//! is a hand-rolled parser/evaluator targeting API version `2024-11-30`.
//!
//! Design note: the .NET engine materializes the whole container per query
//! (bounded by a semaphore). The Rust port should use bounded/streaming
//! execution from the start to avoid the historical memory blow-up.

/// Placeholder query result. Filled in during the `query-crate` phase.
#[derive(Debug, Default)]
pub struct QueryResult {
    pub documents: Vec<serde_json::Value>,
}

/// Entry point for executing a Cosmos SQL query (stub).
pub fn execute_sql(_query: &str) -> Result<QueryResult, anyhow::Error> {
    Ok(QueryResult::default())
}
