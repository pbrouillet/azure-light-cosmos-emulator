//! Expression evaluation and query execution for the Cosmos SQL subset.

use std::cmp::Ordering;
use std::collections::HashMap;

use serde_json::{Map, Value};

use crate::ast::*;
use crate::functions;
use crate::value::{compare_relational, equals, is_true, number, total_cmp, QVal};

/// Evaluation context: the root alias bound to the current document plus query
/// parameters.
struct Ctx<'a> {
    alias: &'a str,
    doc: &'a Value,
    params: &'a HashMap<String, Value>,
}

/// The outcome of executing a query: the projected rows (each a JSON object)
/// and whether the projection was `SELECT VALUE`.
pub struct ExecResult {
    pub rows: Vec<Map<String, Value>>,
    pub is_value: bool,
}

/// Executes a parsed statement against a set of documents.
pub fn execute(
    stmt: &SelectStmt,
    docs: &[Value],
    params: &HashMap<String, Value>,
) -> Result<ExecResult, String> {
    // 1. Filter (WHERE).
    let mut matched: Vec<&Value> = Vec::new();
    for doc in docs {
        let ctx = Ctx {
            alias: &stmt.from_alias,
            doc,
            params,
        };
        let keep = match &stmt.where_clause {
            Some(pred) => is_true(&eval(pred, &ctx)?),
            None => true,
        };
        if keep {
            matched.push(doc);
        }
    }

    // 2. Aggregates short-circuit ordering/pagination.
    if is_aggregate_projection(&stmt.projection) {
        let row = compute_aggregates(stmt, &matched, params)?;
        let is_value = matches!(stmt.projection, Projection::Value(_));
        return Ok(ExecResult {
            rows: vec![row],
            is_value,
        });
    }

    // 3. ORDER BY (evaluated against source documents).
    if !stmt.order_by.is_empty() {
        let mut keyed: Vec<(Vec<QVal>, &Value)> = Vec::with_capacity(matched.len());
        for doc in &matched {
            let ctx = Ctx {
                alias: &stmt.from_alias,
                doc,
                params,
            };
            let mut keys = Vec::with_capacity(stmt.order_by.len());
            for (expr, _) in &stmt.order_by {
                keys.push(eval(expr, &ctx)?);
            }
            keyed.push((keys, doc));
        }
        keyed.sort_by(|a, b| {
            for (idx, (_, desc)) in stmt.order_by.iter().enumerate() {
                let ord = total_cmp(&a.0[idx], &b.0[idx]);
                let ord = if *desc { ord.reverse() } else { ord };
                if ord != Ordering::Equal {
                    return ord;
                }
            }
            Ordering::Equal
        });
        matched = keyed.into_iter().map(|(_, d)| d).collect();
    }

    // 4. Project.
    let mut rows: Vec<Map<String, Value>> = Vec::new();
    let is_value = matches!(stmt.projection, Projection::Value(_));
    for doc in &matched {
        let ctx = Ctx {
            alias: &stmt.from_alias,
            doc,
            params,
        };
        if let Some(row) = project(&stmt.projection, &ctx)? {
            rows.push(row);
        }
    }

    // 5. DISTINCT.
    if stmt.distinct {
        let mut seen = std::collections::HashSet::new();
        rows.retain(|r| {
            let key = serde_json::to_string(r).unwrap_or_default();
            seen.insert(key)
        });
    }

    // 6. OFFSET / LIMIT / TOP.
    let offset = eval_count(stmt.offset.as_ref(), params)?.unwrap_or(0);
    if offset > 0 {
        rows.drain(0..offset.min(rows.len()));
    }
    if let Some(limit) = eval_count(stmt.limit.as_ref(), params)? {
        rows.truncate(limit);
    }
    if let Some(top) = eval_count(stmt.top.as_ref(), params)? {
        rows.truncate(top);
    }

    Ok(ExecResult { rows, is_value })
}

fn eval_count(
    expr: Option<&Expr>,
    params: &HashMap<String, Value>,
) -> Result<Option<usize>, String> {
    let Some(expr) = expr else {
        return Ok(None);
    };
    let ctx = Ctx {
        alias: "",
        doc: &Value::Null,
        params,
    };
    match eval(expr, &ctx)? {
        Some(Value::Number(n)) => Ok(Some(n.as_f64().unwrap_or(0.0).max(0.0) as usize)),
        _ => Err("TOP/OFFSET/LIMIT must be a number".into()),
    }
}

fn is_aggregate_projection(projection: &Projection) -> bool {
    match projection {
        Projection::Value(expr) => is_aggregate_expr(expr),
        Projection::Items(items) => items.iter().any(|i| is_aggregate_expr(&i.expr)),
        Projection::Star => false,
    }
}

fn is_aggregate_expr(expr: &Expr) -> bool {
    matches!(expr, Expr::Call { name, .. } if functions::is_aggregate(name))
}

fn compute_aggregates(
    stmt: &SelectStmt,
    docs: &[&Value],
    params: &HashMap<String, Value>,
) -> Result<Map<String, Value>, String> {
    let mut row = Map::new();
    match &stmt.projection {
        Projection::Value(expr) => {
            let v = eval_aggregate(expr, stmt, docs, params)?;
            row.insert("$1".into(), v.unwrap_or(Value::Null));
        }
        Projection::Items(items) => {
            for (idx, item) in items.iter().enumerate() {
                let name = item
                    .alias
                    .clone()
                    .unwrap_or_else(|| format!("${}", idx + 1));
                let v = if is_aggregate_expr(&item.expr) {
                    eval_aggregate(&item.expr, stmt, docs, params)?
                } else {
                    // Constant / scalar item over the first row (rare in aggregate mode).
                    None
                };
                if let Some(v) = v {
                    row.insert(name, v);
                }
            }
        }
        Projection::Star => {}
    }
    Ok(row)
}

fn eval_aggregate(
    expr: &Expr,
    stmt: &SelectStmt,
    docs: &[&Value],
    params: &HashMap<String, Value>,
) -> Result<QVal, String> {
    let Expr::Call { name, args } = expr else {
        return Ok(None);
    };
    let upper = name.to_ascii_uppercase();
    let inner = args.first();

    // Evaluate the argument for each document.
    let mut values: Vec<QVal> = Vec::new();
    for doc in docs {
        let ctx = Ctx {
            alias: &stmt.from_alias,
            doc,
            params,
        };
        let v = match inner {
            Some(e) => eval(e, &ctx)?,
            None => Some(Value::Null),
        };
        values.push(v);
    }

    let result = match upper.as_str() {
        "COUNT" => {
            let count = match inner {
                // COUNT(1) / COUNT(*) — count all rows.
                Some(Expr::Lit(_)) | None => docs.len(),
                // COUNT(expr) — count defined (non-undefined) values.
                _ => values.iter().filter(|v| v.is_some()).count(),
            };
            Some(Value::from(count as i64))
        }
        "SUM" => {
            let nums: Vec<f64> = values.iter().filter_map(crate::value::as_f64).collect();
            if nums.is_empty() {
                None
            } else {
                number(nums.iter().sum())
            }
        }
        "AVG" => {
            let nums: Vec<f64> = values.iter().filter_map(crate::value::as_f64).collect();
            if nums.is_empty() {
                None
            } else {
                number(nums.iter().sum::<f64>() / nums.len() as f64)
            }
        }
        "MIN" => values
            .iter()
            .filter(|v| v.is_some())
            .min_by(|a, b| total_cmp(a, b))
            .cloned()
            .flatten(),
        "MAX" => values
            .iter()
            .filter(|v| v.is_some())
            .max_by(|a, b| total_cmp(a, b))
            .cloned()
            .flatten(),
        _ => None,
    };
    Ok(result)
}

fn project(projection: &Projection, ctx: &Ctx) -> Result<Option<Map<String, Value>>, String> {
    match projection {
        Projection::Star => {
            // `SELECT *` returns the root document object.
            match ctx.doc {
                Value::Object(map) => Ok(Some(map.clone())),
                _ => Ok(None),
            }
        }
        Projection::Value(expr) => {
            let v = eval(expr, ctx)?;
            let mut row = Map::new();
            row.insert("$1".into(), v.unwrap_or(Value::Null));
            Ok(Some(row))
        }
        Projection::Items(items) => {
            let mut row = Map::new();
            for (idx, item) in items.iter().enumerate() {
                let v = eval(&item.expr, ctx)?;
                // Undefined properties are omitted from the output object.
                let Some(v) = v else {
                    continue;
                };
                let name = item
                    .alias
                    .clone()
                    .unwrap_or_else(|| derived_name(&item.expr, idx + 1));
                row.insert(name, v);
            }
            Ok(Some(row))
        }
    }
}

fn derived_name(expr: &Expr, auto: usize) -> String {
    match expr {
        Expr::Member(_, name) => name.clone(),
        Expr::Identifier(name) => name.clone(),
        _ => format!("${auto}"),
    }
}

/// Evaluates a scalar expression against the current context.
fn eval(expr: &Expr, ctx: &Ctx) -> Result<QVal, String> {
    match expr {
        Expr::Lit(v) => Ok(Some(v.clone())),
        Expr::Param(p) => Ok(ctx.params.get(p).cloned()),
        Expr::Identifier(name) => {
            if name == ctx.alias {
                Ok(Some(ctx.doc.clone()))
            } else {
                Ok(None)
            }
        }
        Expr::Member(base, name) => {
            let base = eval(base, ctx)?;
            match base {
                Some(Value::Object(map)) => Ok(map.get(name).cloned()),
                _ => Ok(None),
            }
        }
        Expr::Index(base, idx) => {
            let base = eval(base, ctx)?;
            let idx = eval(idx, ctx)?;
            match (base, idx) {
                (Some(Value::Array(a)), Some(Value::Number(n))) => {
                    let i = n.as_i64().unwrap_or(-1);
                    if i >= 0 && (i as usize) < a.len() {
                        Ok(Some(a[i as usize].clone()))
                    } else {
                        Ok(None)
                    }
                }
                (Some(Value::Object(o)), Some(Value::String(k))) => Ok(o.get(&k).cloned()),
                _ => Ok(None),
            }
        }
        Expr::Unary(op, e) => {
            let v = eval(e, ctx)?;
            Ok(match op {
                UnaryOp::Not => match v {
                    Some(Value::Bool(b)) => Some(Value::Bool(!b)),
                    _ => None,
                },
                UnaryOp::Neg => crate::value::as_f64(&v).and_then(|n| number(-n)),
                UnaryOp::Pos => crate::value::as_f64(&v).and_then(number),
            })
        }
        Expr::Binary(op, l, r) => eval_binary(*op, l, r, ctx),
        Expr::Between {
            expr,
            lo,
            hi,
            negated,
        } => {
            let v = eval(expr, ctx)?;
            let lo = eval(lo, ctx)?;
            let hi = eval(hi, ctx)?;
            let ge_lo = compare_relational(&v, &lo).map(|o| o != Ordering::Less);
            let le_hi = compare_relational(&v, &hi).map(|o| o != Ordering::Greater);
            let in_range = match (ge_lo, le_hi) {
                (Some(a), Some(b)) => Some(a && b),
                _ => None,
            };
            Ok(in_range.map(|b| Value::Bool(b ^ negated)))
        }
        Expr::In {
            expr,
            items,
            negated,
        } => {
            let v = eval(expr, ctx)?;
            if v.is_none() {
                return Ok(None);
            }
            let mut found = false;
            for item in items {
                let iv = eval(item, ctx)?;
                if equals(&v, &iv) == Some(true) {
                    found = true;
                    break;
                }
            }
            Ok(Some(Value::Bool(found ^ negated)))
        }
        Expr::Call { name, args } => {
            if functions::is_aggregate(name) {
                return Err(format!(
                    "Aggregate {name} is only valid in the SELECT projection"
                ));
            }
            let mut evaluated = Vec::with_capacity(args.len());
            for a in args {
                evaluated.push(eval(a, ctx)?);
            }
            functions::call(name, &evaluated)
        }
        Expr::Array(items) => {
            let mut out = Vec::new();
            for item in items {
                if let Some(v) = eval(item, ctx)? {
                    out.push(v);
                }
            }
            Ok(Some(Value::Array(out)))
        }
        Expr::Object(fields) => {
            let mut map = Map::new();
            for (k, e) in fields {
                if let Some(v) = eval(e, ctx)? {
                    map.insert(k.clone(), v);
                }
            }
            Ok(Some(Value::Object(map)))
        }
    }
}

fn eval_binary(op: BinOp, l: &Expr, r: &Expr, ctx: &Ctx) -> Result<QVal, String> {
    // Short-circuit three-valued logic for AND/OR.
    if op == BinOp::And {
        let lv = eval(l, ctx)?;
        if matches!(lv, Some(Value::Bool(false))) {
            return Ok(Some(Value::Bool(false)));
        }
        let rv = eval(r, ctx)?;
        return Ok(match (lv, rv) {
            (Some(Value::Bool(true)), Some(Value::Bool(true))) => Some(Value::Bool(true)),
            (_, Some(Value::Bool(false))) => Some(Value::Bool(false)),
            _ => None,
        });
    }
    if op == BinOp::Or {
        let lv = eval(l, ctx)?;
        if matches!(lv, Some(Value::Bool(true))) {
            return Ok(Some(Value::Bool(true)));
        }
        let rv = eval(r, ctx)?;
        return Ok(match (lv, rv) {
            (Some(Value::Bool(false)), Some(Value::Bool(false))) => Some(Value::Bool(false)),
            (_, Some(Value::Bool(true))) => Some(Value::Bool(true)),
            _ => None,
        });
    }

    let lv = eval(l, ctx)?;
    let rv = eval(r, ctx)?;
    Ok(match op {
        BinOp::Eq => equals(&lv, &rv).map(Value::Bool),
        BinOp::Ne => equals(&lv, &rv).map(|b| Value::Bool(!b)),
        BinOp::Lt => compare_relational(&lv, &rv).map(|o| Value::Bool(o == Ordering::Less)),
        BinOp::Le => compare_relational(&lv, &rv).map(|o| Value::Bool(o != Ordering::Greater)),
        BinOp::Gt => compare_relational(&lv, &rv).map(|o| Value::Bool(o == Ordering::Greater)),
        BinOp::Ge => compare_relational(&lv, &rv).map(|o| Value::Bool(o != Ordering::Less)),
        BinOp::Add => arith(&lv, &rv, |a, b| a + b),
        BinOp::Sub => arith(&lv, &rv, |a, b| a - b),
        BinOp::Mul => arith(&lv, &rv, |a, b| a * b),
        BinOp::Div => arith(&lv, &rv, |a, b| a / b),
        BinOp::Mod => arith(&lv, &rv, |a, b| a % b),
        BinOp::Concat => match (lv, rv) {
            (Some(Value::String(a)), Some(Value::String(b))) => Some(Value::String(a + &b)),
            _ => None,
        },
        BinOp::And | BinOp::Or => unreachable!(),
    })
}

fn arith(l: &QVal, r: &QVal, f: impl Fn(f64, f64) -> f64) -> QVal {
    match (crate::value::as_f64(l), crate::value::as_f64(r)) {
        (Some(a), Some(b)) => number(f(a, b)),
        _ => None,
    }
}
