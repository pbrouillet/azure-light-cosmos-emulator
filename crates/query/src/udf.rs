//! User-defined function resolution for SQL `udf.<name>(...)` calls.

use serde_json::Value;

/// Pluggable SQL UDF evaluator.
///
/// The query crate stays storage/runtime agnostic: callers may inject an
/// implementation that loads registered UDF JavaScript and executes it. `None`
/// means Cosmos `Undefined` (missing UDF, script error, or no resolver).
pub trait UdfResolver: Send + Sync {
    fn eval(
        &self,
        database_id: &str,
        container_id: &str,
        name: &str,
        args: &[Value],
    ) -> Option<Value>;
}
