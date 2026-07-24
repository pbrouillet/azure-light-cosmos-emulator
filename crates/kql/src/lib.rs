//! KQL pipeline execution for the Cosmos light emulator.
//!
//! This crate ports the .NET `Kql` project: a table-resolved pipeline executor,
//! schema registry, expression evaluator, and the common tabular operators used
//! by monitoring queries.

mod ast;
mod error;
mod evaluator;
mod executor;
mod lexer;
mod operators;
mod parser;
mod result;
mod schema;
mod value;

pub use error::{KqlError, KqlResult};
pub use evaluator::ExpressionEvaluator;
pub use executor::KqlQueryExecutor;
pub use operators::*;
pub use result::{KqlQueryResult, Row};
pub use schema::{KqlColumnSchema, KqlSchemaRegistry, KqlTableSchema};

/// Parses and executes a KQL query against an in-memory table resolver.
pub fn execute_query<F>(kql: &str, table_resolver: F) -> KqlResult<KqlQueryResult>
where
    F: Fn(&str) -> KqlResult<Vec<Row>>,
{
    KqlQueryExecutor::new(KqlSchemaRegistry::default()).execute(kql, table_resolver)
}
