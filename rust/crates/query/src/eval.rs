//! Expression evaluation and query execution for the Cosmos SQL subset.

use std::cmp::Ordering;
use std::collections::HashMap;

use serde_json::{Map, Value};

use crate::ast::*;
use crate::functions;
use crate::udf::UdfResolver;
use crate::value::{compare_relational, equals, is_true, number, total_cmp, QVal};

#[derive(Clone, Debug, Default)]
struct QueryRow {
    aliases: HashMap<String, Value>,
}

impl QueryRow {
    fn get(&self, alias: &str) -> Option<Value> {
        self.aliases.get(&alias.to_ascii_lowercase()).cloned()
    }

    fn with_alias(&self, alias: &str, value: Value) -> Self {
        let mut next = self.clone();
        next.aliases.insert(alias.to_ascii_lowercase(), value);
        next
    }
}

#[derive(Clone, Copy, Default)]
struct EvalConfig<'a> {
    database_id: Option<&'a str>,
    container_id: Option<&'a str>,
    udf_resolver: Option<&'a dyn UdfResolver>,
}

/// Evaluation context: current alias row, all source documents (for subqueries),
/// optional aggregate group, query parameters, and injected services.
struct Ctx<'a> {
    row: Option<&'a QueryRow>,
    docs: &'a [Value],
    params: &'a HashMap<String, Value>,
    group: Option<&'a [QueryRow]>,
    cfg: EvalConfig<'a>,
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
    execute_with_udf_resolver(stmt, docs, params, None, None, None)
}

pub fn execute_with_udf_resolver(
    stmt: &SelectStmt,
    docs: &[Value],
    params: &HashMap<String, Value>,
    database_id: Option<&str>,
    container_id: Option<&str>,
    udf_resolver: Option<&dyn UdfResolver>,
) -> Result<ExecResult, String> {
    execute_select(
        stmt,
        docs,
        params,
        None,
        EvalConfig {
            database_id,
            container_id,
            udf_resolver,
        },
    )
}

fn execute_select(
    stmt: &SelectStmt,
    docs: &[Value],
    params: &HashMap<String, Value>,
    outer: Option<&QueryRow>,
    cfg: EvalConfig<'_>,
) -> Result<ExecResult, String> {
    let mut rows = build_source_rows(stmt, docs, params, outer, cfg)?;

    if let Some(pred) = &stmt.where_clause {
        rows = rows
            .into_iter()
            .filter_map(|row| {
                let ctx = Ctx {
                    row: Some(&row),
                    docs,
                    params,
                    group: None,
                    cfg,
                };
                match eval(pred, &ctx) {
                    Ok(v) if is_true(&v) => Some(Ok(row)),
                    Ok(_) => None,
                    Err(e) => Some(Err(e)),
                }
            })
            .collect::<Result<Vec<_>, _>>()?;
    }

    let aggregate_mode = !stmt.group_by.is_empty() || is_aggregate_projection(&stmt.projection);
    if aggregate_mode {
        let mut projected = execute_grouped(stmt, rows, docs, params, cfg)?;
        apply_projected_ordering(&mut projected, stmt, docs, params, cfg)?;
        apply_distinct_and_window(&mut projected, stmt, params)?;
        return Ok(ExecResult {
            rows: projected,
            is_value: matches!(stmt.projection, Projection::Value(_)),
        });
    }

    apply_row_ordering(&mut rows, stmt, docs, params, cfg)?;

    let mut projected = Vec::new();
    for row in &rows {
        let ctx = Ctx {
            row: Some(row),
            docs,
            params,
            group: None,
            cfg,
        };
        if let Some(out) = project(&stmt.projection, &ctx)? {
            projected.push(out);
        }
    }

    apply_distinct_and_window(&mut projected, stmt, params)?;
    Ok(ExecResult {
        rows: projected,
        is_value: matches!(stmt.projection, Projection::Value(_)),
    })
}

fn build_source_rows(
    stmt: &SelectStmt,
    docs: &[Value],
    params: &HashMap<String, Value>,
    outer: Option<&QueryRow>,
    cfg: EvalConfig<'_>,
) -> Result<Vec<QueryRow>, String> {
    let mut seeds = Vec::new();

    if let Some(source) = &stmt.from_in {
        // Correlated FROM ... IN: evaluate once against the outer row. Top-level
        // array iteration evaluates the source against each root document.
        if let Some(outer_row) = outer {
            let ctx = Ctx {
                row: Some(outer_row),
                docs,
                params,
                group: None,
                cfg,
            };
            expand_array_source(&mut seeds, outer_row, &stmt.from_alias, eval(source, &ctx)?)?;
        } else {
            for doc in docs {
                let mut base = outer.cloned().unwrap_or_default();
                base.aliases.insert("c".into(), doc.clone());
                base.aliases
                    .insert(stmt.from_alias.to_ascii_lowercase(), doc.clone());
                let ctx = Ctx {
                    row: Some(&base),
                    docs,
                    params,
                    group: None,
                    cfg,
                };
                expand_array_source(&mut seeds, &base, &stmt.from_alias, eval(source, &ctx)?)?;
            }
        }
    } else if docs.is_empty() && outer.is_some() {
        seeds.push(outer.cloned().unwrap_or_default());
    } else {
        for doc in docs {
            let mut aliases = outer.cloned().unwrap_or_default().aliases;
            aliases.insert(stmt.from_alias.to_ascii_lowercase(), doc.clone());
            seeds.push(QueryRow { aliases });
        }
    }

    if stmt.joins.is_empty() {
        return Ok(seeds);
    }

    let mut rows = seeds;
    for join in &stmt.joins {
        let mut expanded = Vec::new();
        for row in &rows {
            let ctx = Ctx {
                row: Some(row),
                docs,
                params,
                group: None,
                cfg,
            };
            if let Some(Value::Array(items)) = eval(&join.source, &ctx)? {
                for item in items {
                    expanded.push(row.with_alias(&join.alias, item));
                }
            }
        }
        rows = expanded;
        if rows.is_empty() {
            break;
        }
    }

    Ok(rows)
}

fn expand_array_source(
    rows: &mut Vec<QueryRow>,
    base: &QueryRow,
    alias: &str,
    source: QVal,
) -> Result<(), String> {
    if let Some(Value::Array(items)) = source {
        for item in items {
            rows.push(base.with_alias(alias, item));
        }
    }
    Ok(())
}

fn apply_row_ordering(
    rows: &mut Vec<QueryRow>,
    stmt: &SelectStmt,
    docs: &[Value],
    params: &HashMap<String, Value>,
    cfg: EvalConfig<'_>,
) -> Result<(), String> {
    if stmt.order_by.is_empty() || rows.is_empty() {
        return Ok(());
    }

    let vector_order = matches!(
        &stmt.order_by[0].0,
        Expr::Call { name, .. } if name.eq_ignore_ascii_case("VectorDistance")
    );
    if vector_order {
        let (expr, desc) = &stmt.order_by[0];
        let metric = vector_metric(expr, None);
        let mut keyed = Vec::new();
        for row in rows.drain(..) {
            let ctx = Ctx {
                row: Some(&row),
                docs,
                params,
                group: None,
                cfg,
            };
            let value = eval(expr, &ctx)?;
            if let Some(Value::Number(n)) = value {
                let raw = n.as_f64().unwrap_or(0.0);
                let nearest_key = if metric == VectorMetric::Euclidean {
                    raw
                } else {
                    -raw
                };
                keyed.push((nearest_key, row));
            }
        }
        keyed.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap_or(Ordering::Equal));
        if *desc {
            keyed.reverse();
        }
        *rows = keyed.into_iter().map(|(_, row)| row).collect();
        return Ok(());
    }

    let mut keyed: Vec<(Vec<QVal>, QueryRow)> = Vec::with_capacity(rows.len());
    for row in rows.drain(..) {
        let ctx = Ctx {
            row: Some(&row),
            docs,
            params,
            group: None,
            cfg,
        };
        let mut keys = Vec::with_capacity(stmt.order_by.len());
        for (expr, _) in &stmt.order_by {
            keys.push(eval(expr, &ctx)?);
        }
        keyed.push((keys, row));
    }
    keyed.sort_by(|a, b| compare_keys(&a.0, &b.0, &stmt.order_by));
    *rows = keyed.into_iter().map(|(_, row)| row).collect();
    Ok(())
}

fn compare_keys(a: &[QVal], b: &[QVal], order_by: &[(Expr, bool)]) -> Ordering {
    for (idx, (_, desc)) in order_by.iter().enumerate() {
        let ord = total_cmp(&a[idx], &b[idx]);
        let ord = if *desc { ord.reverse() } else { ord };
        if ord != Ordering::Equal {
            return ord;
        }
    }
    Ordering::Equal
}

fn apply_projected_ordering(
    rows: &mut Vec<Map<String, Value>>,
    stmt: &SelectStmt,
    docs: &[Value],
    params: &HashMap<String, Value>,
    cfg: EvalConfig<'_>,
) -> Result<(), String> {
    if stmt.order_by.is_empty() || rows.is_empty() {
        return Ok(());
    }
    let mut keyed = Vec::with_capacity(rows.len());
    for row in rows.drain(..) {
        let qrow = QueryRow {
            aliases: HashMap::from([(
                stmt.from_alias.to_ascii_lowercase(),
                Value::Object(row.clone()),
            )]),
        };
        let ctx = Ctx {
            row: Some(&qrow),
            docs,
            params,
            group: None,
            cfg,
        };
        let mut keys = Vec::with_capacity(stmt.order_by.len());
        for (expr, _) in &stmt.order_by {
            keys.push(eval(expr, &ctx)?);
        }
        keyed.push((keys, row));
    }
    keyed.sort_by(|a, b| compare_keys(&a.0, &b.0, &stmt.order_by));
    *rows = keyed.into_iter().map(|(_, row)| row).collect();
    Ok(())
}

fn apply_distinct_and_window(
    rows: &mut Vec<Map<String, Value>>,
    stmt: &SelectStmt,
    params: &HashMap<String, Value>,
) -> Result<(), String> {
    if stmt.distinct {
        let mut seen = std::collections::HashSet::new();
        rows.retain(|r| seen.insert(serde_json::to_string(r).unwrap_or_default()));
    }

    if let Some(top) = eval_count(stmt.top.as_ref(), params)? {
        rows.truncate(top);
    }
    let offset = eval_count(stmt.offset.as_ref(), params)?.unwrap_or(0);
    if offset > 0 {
        rows.drain(0..offset.min(rows.len()));
    }
    if let Some(limit) = eval_count(stmt.limit.as_ref(), params)? {
        rows.truncate(limit);
    }
    Ok(())
}

fn eval_count(
    expr: Option<&Expr>,
    params: &HashMap<String, Value>,
) -> Result<Option<usize>, String> {
    let Some(expr) = expr else {
        return Ok(None);
    };
    let ctx = Ctx {
        row: None,
        docs: &[],
        params,
        group: None,
        cfg: EvalConfig::default(),
    };
    match eval(expr, &ctx)? {
        Some(Value::Number(n)) => Ok(Some(n.as_f64().unwrap_or(0.0).max(0.0) as usize)),
        _ => Err("TOP/OFFSET/LIMIT must be a number".into()),
    }
}

fn is_aggregate_projection(projection: &Projection) -> bool {
    match projection {
        Projection::Value(expr) => contains_aggregate_expr(expr),
        Projection::Items(items) => items.iter().any(|i| contains_aggregate_expr(&i.expr)),
        Projection::Star => false,
    }
}

fn contains_aggregate_expr(expr: &Expr) -> bool {
    match expr {
        Expr::Call { name, args } => {
            functions::is_aggregate(name) || args.iter().any(contains_aggregate_expr)
        }
        Expr::Unary(_, e) => contains_aggregate_expr(e),
        Expr::Binary(_, l, r) => contains_aggregate_expr(l) || contains_aggregate_expr(r),
        Expr::Between { expr, lo, hi, .. } => {
            contains_aggregate_expr(expr)
                || contains_aggregate_expr(lo)
                || contains_aggregate_expr(hi)
        }
        Expr::In { expr, items, .. } => {
            contains_aggregate_expr(expr) || items.iter().any(contains_aggregate_expr)
        }
        Expr::Member(base, _) => contains_aggregate_expr(base),
        Expr::Index(base, idx) => contains_aggregate_expr(base) || contains_aggregate_expr(idx),
        Expr::Array(items) => items.iter().any(contains_aggregate_expr),
        Expr::Object(fields) => fields.iter().any(|(_, e)| contains_aggregate_expr(e)),
        Expr::Subquery(_) | Expr::Lit(_) | Expr::Param(_) | Expr::Star | Expr::Identifier(_) => {
            false
        }
    }
}

fn execute_grouped(
    stmt: &SelectStmt,
    rows: Vec<QueryRow>,
    docs: &[Value],
    params: &HashMap<String, Value>,
    cfg: EvalConfig<'_>,
) -> Result<Vec<Map<String, Value>>, String> {
    let groups = build_groups(stmt, rows, docs, params, cfg)?;
    let mut out = Vec::new();
    for group in groups {
        let representative = group.rows.first();
        let ctx = Ctx {
            row: representative,
            docs,
            params,
            group: Some(&group.rows),
            cfg,
        };
        if let Some(row) = project(&stmt.projection, &ctx)? {
            out.push(row);
        }
    }
    Ok(out)
}

struct Group {
    key: Vec<QVal>,
    rows: Vec<QueryRow>,
}

fn build_groups(
    stmt: &SelectStmt,
    rows: Vec<QueryRow>,
    docs: &[Value],
    params: &HashMap<String, Value>,
    cfg: EvalConfig<'_>,
) -> Result<Vec<Group>, String> {
    if stmt.group_by.is_empty() {
        return Ok(vec![Group {
            key: Vec::new(),
            rows,
        }]);
    }
    let mut groups: Vec<Group> = Vec::new();
    for row in rows {
        let ctx = Ctx {
            row: Some(&row),
            docs,
            params,
            group: None,
            cfg,
        };
        let key = stmt
            .group_by
            .iter()
            .map(|e| eval(e, &ctx))
            .collect::<Result<Vec<_>, _>>()?;
        if let Some(existing) = groups.iter_mut().find(|g| keys_equal(&g.key, &key)) {
            existing.rows.push(row);
        } else {
            groups.push(Group {
                key,
                rows: vec![row],
            });
        }
    }
    Ok(groups)
}

fn keys_equal(a: &[QVal], b: &[QVal]) -> bool {
    a.len() == b.len()
        && a.iter().zip(b).all(|(left, right)| match (left, right) {
            (None, None) => true,
            _ => equals(left, right) == Some(true),
        })
}

fn eval_aggregate_call(name: &str, args: &[Expr], ctx: &Ctx) -> Result<QVal, String> {
    let group = ctx
        .group
        .ok_or_else(|| format!("Aggregate {name} is only valid in aggregate context"))?;
    let upper = name.to_ascii_uppercase();
    if args.len() != 1 {
        return Err(format!("Function '{name}' expects a single argument"));
    }
    let argument = &args[0];
    if upper == "COUNT" {
        let count = match argument {
            Expr::Star | Expr::Lit(_) => group.len(),
            _ => group
                .iter()
                .filter(|row| {
                    let inner = Ctx {
                        row: Some(row),
                        docs: ctx.docs,
                        params: ctx.params,
                        group: None,
                        cfg: ctx.cfg,
                    };
                    matches!(eval(argument, &inner), Ok(Some(v)) if !v.is_null())
                })
                .count(),
        };
        return Ok(Some(Value::from(count as i64)));
    }

    let mut values = Vec::new();
    for row in group {
        let inner = Ctx {
            row: Some(row),
            docs: ctx.docs,
            params: ctx.params,
            group: None,
            cfg: ctx.cfg,
        };
        if let Some(v) = eval(argument, &inner)? {
            if !v.is_null() {
                values.push(Some(v));
            }
        }
    }

    let result = match upper.as_str() {
        "SUM" => {
            let nums: Vec<f64> = values.iter().filter_map(crate::value::as_f64).collect();
            if nums.is_empty() {
                Some(Value::Null)
            } else {
                number(nums.iter().sum()).or(Some(Value::Null))
            }
        }
        "AVG" => {
            let nums: Vec<f64> = values.iter().filter_map(crate::value::as_f64).collect();
            if nums.is_empty() {
                Some(Value::Null)
            } else {
                number(nums.iter().sum::<f64>() / nums.len() as f64).or(Some(Value::Null))
            }
        }
        "MIN" => values
            .iter()
            .min_by(|a, b| total_cmp(a, b))
            .cloned()
            .flatten()
            .or(Some(Value::Null)),
        "MAX" => values
            .iter()
            .max_by(|a, b| total_cmp(a, b))
            .cloned()
            .flatten()
            .or(Some(Value::Null)),
        _ => None,
    };
    Ok(result)
}

fn project(projection: &Projection, ctx: &Ctx) -> Result<Option<Map<String, Value>>, String> {
    match projection {
        Projection::Star => {
            let Some(row) = ctx.row else {
                return Ok(None);
            };
            match row
                .get("c")
                .or_else(|| row.aliases.values().next().cloned())
            {
                Some(Value::Object(map)) => Ok(Some(map)),
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
        Expr::Star => Ok(None),
        Expr::Identifier(name) => Ok(ctx.row.and_then(|r| r.get(name))),
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
                return eval_aggregate_call(name, args, ctx);
            }
            let mut evaluated = Vec::with_capacity(args.len());
            for a in args {
                evaluated.push(eval(a, ctx)?);
            }
            if name.len() > 4 && name[..4].eq_ignore_ascii_case("udf.") {
                let udf_name = &name[4..];
                let (Some(database_id), Some(container_id), Some(resolver)) = (
                    ctx.cfg.database_id,
                    ctx.cfg.container_id,
                    ctx.cfg.udf_resolver,
                ) else {
                    return Ok(None);
                };
                let args: Vec<Value> = evaluated
                    .into_iter()
                    .map(|value| value.unwrap_or(Value::Null))
                    .collect();
                return Ok(resolver.eval(database_id, container_id, udf_name, &args));
            }
            functions::call(name, &evaluated)
        }
        Expr::Subquery(stmt) => {
            let Some(row) = ctx.row else {
                return Ok(None);
            };
            if let Some(source) = &stmt.from_in {
                let source_ctx = Ctx {
                    row: Some(row),
                    docs: ctx.docs,
                    params: ctx.params,
                    group: None,
                    cfg: ctx.cfg,
                };
                if !matches!(eval(source, &source_ctx)?, Some(Value::Array(_))) {
                    return Ok(None);
                }
            }
            let result = execute_select(stmt, ctx.docs, ctx.params, Some(row), ctx.cfg)?;
            if result.rows.is_empty() {
                return Ok(None);
            }
            if result.rows.len() > 1 {
                return Err("Scalar subquery must return at most one row".into());
            }
            let row = &result.rows[0];
            if row.len() == 1 && row.contains_key("$1") {
                Ok(row.get("$1").cloned())
            } else {
                Ok(Some(Value::Object(row.clone())))
            }
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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum VectorMetric {
    Cosine,
    DotProduct,
    Euclidean,
}

fn vector_metric(expr: &Expr, evaluated_options: Option<&Value>) -> VectorMetric {
    if let Some(Value::Object(options)) = evaluated_options {
        if let Some(Value::String(df)) = options.get("distanceFunction") {
            return parse_metric(df);
        }
    }
    if let Expr::Call { args, .. } = expr {
        if let Some(Expr::Object(fields)) = args.get(3) {
            if let Some((_, Expr::Lit(Value::String(df)))) = fields
                .iter()
                .find(|(k, _)| k.eq_ignore_ascii_case("distanceFunction"))
            {
                return parse_metric(df);
            }
        }
    }
    VectorMetric::Cosine
}

fn parse_metric(value: &str) -> VectorMetric {
    match value.trim().to_ascii_lowercase().as_str() {
        "dotproduct" | "dot product" => VectorMetric::DotProduct,
        "euclidean" => VectorMetric::Euclidean,
        _ => VectorMetric::Cosine,
    }
}
