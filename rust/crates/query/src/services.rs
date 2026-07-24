//! Query explain, index-validation, and execution-limiter support.

use std::sync::Arc;

use cosmos_core::models::policies::SortOrder;
use cosmos_core::models::{IndexingMode, IndexingPolicy};
use serde_json::{json, Value};
use tokio::sync::{OwnedSemaphorePermit, Semaphore};

use crate::ast::{BinOp, Expr, Projection, SelectStmt};
use crate::parser::parse;

#[derive(Debug, Clone, PartialEq)]
pub struct IndexValidationResult {
    pub requires_scan: bool,
    pub is_allowed: bool,
    pub error_message: Option<String>,
    pub ru_multiplier: f64,
}

pub struct IndexValidationService;

impl IndexValidationService {
    pub fn validate_query(
        policy: &IndexingPolicy,
        filter_paths: &[String],
        order_by_paths: &[(String, bool)],
        scan_enabled: bool,
    ) -> IndexValidationResult {
        if policy.indexing_mode == IndexingMode::None {
            if !scan_enabled {
                return IndexValidationResult {
                    requires_scan: true,
                    is_allowed: false,
                    error_message: Some("Queries are not supported when indexing mode is set to None. Please set the x-ms-documentdb-query-enable-scan header to true.".into()),
                    ru_multiplier: 1.0,
                };
            }
            return IndexValidationResult {
                requires_scan: true,
                is_allowed: true,
                error_message: None,
                ru_multiplier: 3.0,
            };
        }

        for path in filter_paths {
            if !Self::is_indexed(path, policy) {
                if !scan_enabled {
                    return IndexValidationResult {
                        requires_scan: true,
                        is_allowed: false,
                        error_message: Some(format!("The query filter on path '{path}' requires a scan because it is excluded from indexing. Please set the x-ms-documentdb-query-enable-scan header to true.")),
                        ru_multiplier: 1.0,
                    };
                }
                return IndexValidationResult {
                    requires_scan: true,
                    is_allowed: true,
                    error_message: None,
                    ru_multiplier: 2.0,
                };
            }
        }

        if order_by_paths.len() >= 2 && !Self::has_matching_composite_index(policy, order_by_paths)
        {
            return IndexValidationResult {
                requires_scan: false,
                is_allowed: false,
                error_message: Some("The order by query does not have a corresponding composite index that it can be served from.".into()),
                ru_multiplier: 1.0,
            };
        }

        IndexValidationResult {
            requires_scan: false,
            is_allowed: true,
            error_message: None,
            ru_multiplier: 1.0,
        }
    }

    pub fn is_indexed(path: &str, policy: &IndexingPolicy) -> bool {
        if policy.indexing_mode == IndexingMode::None {
            return false;
        }
        if policy
            .excluded_paths
            .iter()
            .any(|p| Self::path_matches(&p.path, path))
        {
            return false;
        }
        policy.included_paths.is_empty()
            || policy
                .included_paths
                .iter()
                .any(|p| Self::path_matches(&p.path, path))
    }

    pub fn path_matches(configured: &str, candidate: &str) -> bool {
        let configured = Self::normalize_policy_path(configured);
        let candidate = Self::normalize_policy_path(candidate);
        configured == "/*"
            || candidate == configured
            || candidate.starts_with(configured.trim_end_matches('*'))
    }

    pub fn normalize_policy_path(path: &str) -> String {
        path.replace("/?", "")
            .replace(['"', ' '], "")
            .trim()
            .to_string()
    }

    pub fn has_matching_composite_index(
        policy: &IndexingPolicy,
        order_by_paths: &[(String, bool)],
    ) -> bool {
        let Some(indexes) = &policy.composite_indexes else {
            return false;
        };
        indexes.iter().any(|index| {
            index.paths.len() == order_by_paths.len()
                && index
                    .paths
                    .iter()
                    .zip(order_by_paths)
                    .all(|(index_path, (query_path, desc))| {
                        let expected = if *desc {
                            SortOrder::Descending
                        } else {
                            SortOrder::Ascending
                        };
                        Self::normalize_policy_path(&index_path.path)
                            == Self::normalize_policy_path(query_path)
                            && index_path.order == expected
                    })
        })
    }

    pub fn convert_to_index_path(query_path: &str) -> Option<String> {
        let mut path = query_path.trim();
        if path.is_empty() {
            return None;
        }
        if path.starts_with('/') {
            return Some(path.to_string());
        }
        if let Some(dot) = path.find('.') {
            path = &path[dot + 1..];
        }
        Some(format!("/{}", path.replace('.', "/")))
    }
}

#[derive(Clone)]
pub struct QueryExecutionLimiter {
    gate: Arc<Semaphore>,
}

impl QueryExecutionLimiter {
    pub fn new(max_concurrency: usize) -> Self {
        Self {
            gate: Arc::new(Semaphore::new(max_concurrency.max(1))),
        }
    }

    pub fn default_max_concurrency() -> usize {
        std::thread::available_parallelism().map_or(2, |n| (n.get() / 2).max(2))
    }

    pub fn new_default() -> Self {
        Self::new(Self::default_max_concurrency())
    }

    pub async fn acquire(&self) -> QueryPermit {
        QueryPermit {
            _permit: self
                .gate
                .clone()
                .acquire_owned()
                .await
                .expect("query limiter semaphore closed"),
        }
    }

    /// Number of permits currently available (i.e. concurrent queries that can
    /// start without waiting). Primarily for tests/diagnostics.
    pub fn available_permits(&self) -> usize {
        self.gate.available_permits()
    }
}

pub struct QueryPermit {
    _permit: OwnedSemaphorePermit,
}

pub struct QueryExplainService;

impl QueryExplainService {
    pub fn explain(query: &str) -> Result<Value, String> {
        let stmt = parse(query)?;
        Ok(Self::explain_stmt(&stmt))
    }

    fn explain_stmt(stmt: &SelectStmt) -> Value {
        let mut plan = serde_json::Map::new();
        plan.insert("operation".into(), Value::String("SELECT".into()));
        plan.insert("fromAlias".into(), Value::String(stmt.from_alias.clone()));
        if !stmt.joins.is_empty() {
            plan.insert(
                "joins".into(),
                json!(stmt
                    .joins
                    .iter()
                    .map(|j| j.alias.clone())
                    .collect::<Vec<_>>()),
            );
        }
        if !stmt.group_by.is_empty() {
            plan.insert(
                "groupBy".into(),
                json!(stmt.group_by.iter().map(format_expr).collect::<Vec<_>>()),
            );
        }
        if !stmt.order_by.is_empty() {
            plan.insert("orderBy".into(), json!(stmt.order_by.iter().map(|(e, d)| json!({"expression": format_expr(e), "direction": if *d {"DESC"} else {"ASC"}})).collect::<Vec<_>>()));
        }
        plan.insert(
            "projection".into(),
            Value::String(
                match stmt.projection {
                    Projection::Star => "all",
                    Projection::Value(_) => "value",
                    Projection::Items(_) => "fields",
                }
                .into(),
            ),
        );
        let mut recommendations = Vec::new();
        let mut warnings = Vec::new();
        let mut notes = Vec::new();
        if !stmt.joins.is_empty() {
            recommendations.push(
                "JOIN operations expand arrays and increase RU cost proportionally to array size",
            );
            notes.push("This query uses an intra-document JOIN");
        }
        if !stmt.group_by.is_empty() {
            warnings
                .push("GROUP BY requires the query engine to buffer all results before returning");
        }
        json!({
            "queryPlan": Value::Object(plan),
            "estimatedCost": { "ru": estimate_cost(stmt) },
            "recommendations": recommendations,
            "warnings": warnings,
            "notes": notes,
        })
    }
}

fn estimate_cost(stmt: &SelectStmt) -> f64 {
    1.0 + if stmt.where_clause.is_some() {
        0.5
    } else {
        0.0
    } + stmt.joins.len() as f64
        + if stmt.group_by.is_empty() { 0.0 } else { 0.5 }
        + if stmt.order_by.is_empty() { 0.0 } else { 0.5 }
}

fn format_expr(expr: &Expr) -> String {
    match expr {
        Expr::Lit(v) => v.to_string(),
        Expr::Param(p) => p.clone(),
        Expr::Star => "*".into(),
        Expr::Identifier(i) => i.clone(),
        Expr::Member(base, name) => format!("{}.{}", format_expr(base), name),
        Expr::Index(base, idx) => format!("{}[{}]", format_expr(base), format_expr(idx)),
        Expr::Unary(_, e) => format_expr(e),
        Expr::Binary(op, l, r) => {
            format!("{} {} {}", format_expr(l), format_op(*op), format_expr(r))
        }
        Expr::Between {
            expr,
            lo,
            hi,
            negated,
        } => format!(
            "{} {}BETWEEN {} AND {}",
            format_expr(expr),
            if *negated { "NOT " } else { "" },
            format_expr(lo),
            format_expr(hi)
        ),
        Expr::In {
            expr,
            items,
            negated,
        } => format!(
            "{} {}IN ({})",
            format_expr(expr),
            if *negated { "NOT " } else { "" },
            items.iter().map(format_expr).collect::<Vec<_>>().join(", ")
        ),
        Expr::Call { name, args } => format!(
            "{}({})",
            name,
            args.iter().map(format_expr).collect::<Vec<_>>().join(", ")
        ),
        Expr::Subquery(_) => "(SELECT ...)".into(),
        Expr::Array(_) => "[...]".into(),
        Expr::Object(_) => "{...}".into(),
    }
}

fn format_op(op: BinOp) -> &'static str {
    match op {
        BinOp::Eq => "=",
        BinOp::Ne => "!=",
        BinOp::Lt => "<",
        BinOp::Le => "<=",
        BinOp::Gt => ">",
        BinOp::Ge => ">=",
        BinOp::Add => "+",
        BinOp::Sub => "-",
        BinOp::Mul => "*",
        BinOp::Div => "/",
        BinOp::Mod => "%",
        BinOp::And => "AND",
        BinOp::Or => "OR",
        BinOp::Concat => "||",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use cosmos_core::models::policies::{CompositeIndexPath, ExcludedPath};
    use cosmos_core::models::CompositeIndex;

    #[test]
    fn limiter_new_sets_capacity_and_bounds_concurrency() {
        let limiter = QueryExecutionLimiter::new(3);
        assert_eq!(limiter.available_permits(), 3);
    }

    #[test]
    fn limiter_new_clamps_zero_to_one() {
        let limiter = QueryExecutionLimiter::new(0);
        assert_eq!(limiter.available_permits(), 1);
    }

    #[tokio::test]
    async fn limiter_serializes_when_capacity_is_one() {
        let limiter = QueryExecutionLimiter::new(1);
        let permit = limiter.acquire().await;
        assert_eq!(limiter.available_permits(), 0);
        // A second acquire must not complete while the first permit is held.
        let blocked =
            tokio::time::timeout(std::time::Duration::from_millis(50), limiter.acquire()).await;
        assert!(
            blocked.is_err(),
            "second acquire should block at capacity 1"
        );
        drop(permit);
        assert_eq!(limiter.available_permits(), 1);
    }

    #[test]
    fn validates_excluded_index_path() {
        let mut policy = IndexingPolicy::default();
        policy.excluded_paths.push(ExcludedPath {
            path: "/secret/?".into(),
        });
        let result =
            IndexValidationService::validate_query(&policy, &["/secret".into()], &[], false);
        assert!(!result.is_allowed);
        let result =
            IndexValidationService::validate_query(&policy, &["/secret".into()], &[], true);
        assert!(result.is_allowed);
        assert!(result.requires_scan);
    }

    #[test]
    fn validates_composite_index_order() {
        let policy = IndexingPolicy {
            composite_indexes: Some(vec![CompositeIndex {
                paths: vec![
                    CompositeIndexPath {
                        path: "/a".into(),
                        order: SortOrder::Ascending,
                    },
                    CompositeIndexPath {
                        path: "/b".into(),
                        order: SortOrder::Descending,
                    },
                ],
            }]),
            ..Default::default()
        };
        let result = IndexValidationService::validate_query(
            &policy,
            &[],
            &[("/a".into(), false), ("/b".into(), true)],
            false,
        );
        assert!(result.is_allowed);
    }

    #[test]
    fn explain_mentions_join_and_group_by() {
        let explanation = QueryExplainService::explain(
            "SELECT c.city, COUNT(1) FROM c JOIN t IN c.tags GROUP BY c.city ORDER BY c.city",
        )
        .unwrap();
        assert!(explanation["recommendations"].as_array().unwrap()[0]
            .as_str()
            .unwrap()
            .contains("JOIN"));
        assert!(explanation["warnings"].as_array().unwrap()[0]
            .as_str()
            .unwrap()
            .contains("GROUP BY"));
    }
}
