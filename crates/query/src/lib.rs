//! Query engines for the Cosmos DB light emulator.
//!
//! Ports the Cosmos SQL engine from `NoSql/Query/CosmosQueryEngine.cs`. This
//! is a hand-rolled tokenizer + recursive-descent parser + tree-walking
//! evaluator supporting the common SQL surface:
//!
//! * `SELECT * | VALUE <expr> | <items>` with aliases and `DISTINCT`
//! * `FROM <alias>` with optional root/alias forms
//! * `WHERE` with comparison, logical (three-valued), arithmetic, `IN`,
//!   `BETWEEN`, and member/index access
//! * `ORDER BY` (multi-key, ASC/DESC) using the Cosmos total ordering
//! * `TOP`, `OFFSET ... LIMIT ...`
//! * parameters (`@name`), array/object literals
//! * scalar functions (string, type-check, math, array, conditional) and the
//!   `COUNT/SUM/AVG/MIN/MAX` aggregates
//!
//! Design note: the .NET engine materializes the whole container per query
//! (bounded by a semaphore). This port keeps that materialization model for
//! now; streaming/bounded execution is a future refinement.

mod ast;
mod dml;
mod engine;
mod eval;
mod functions;
mod lexer;
mod parser;
mod services;
mod udf;
mod value;

pub use dml::DmlCommandService;
pub use engine::SqlQueryEngine;
pub use parser::parse;
pub use services::{
    IndexValidationResult, IndexValidationService, QueryExecutionLimiter, QueryExplainService,
};
pub use udf::UdfResolver;

/// Convenience: parse and execute a query against in-memory JSON documents,
/// returning the projected rows. Primarily used by tests and tooling.
pub fn run_query(
    query: &str,
    docs: &[serde_json::Value],
    params: &std::collections::HashMap<String, serde_json::Value>,
) -> Result<Vec<serde_json::Value>, String> {
    let stmt = parser::parse(query)?;
    let result = eval::execute(&stmt, docs, params)?;
    Ok(result
        .rows
        .into_iter()
        .map(serde_json::Value::Object)
        .collect())
}

/// Convenience: parse and execute a query with an optional SQL UDF resolver.
pub fn run_query_with_udf_resolver(
    query: &str,
    docs: &[serde_json::Value],
    params: &std::collections::HashMap<String, serde_json::Value>,
    udf_resolver: Option<&dyn UdfResolver>,
) -> Result<Vec<serde_json::Value>, String> {
    let stmt = parser::parse(query)?;
    let result = eval::execute_with_udf_resolver(
        &stmt,
        docs,
        params,
        Some("db"),
        Some("coll"),
        udf_resolver,
    )?;
    Ok(result
        .rows
        .into_iter()
        .map(serde_json::Value::Object)
        .collect())
}
