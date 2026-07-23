use std::collections::HashSet;

use serde_json::Value;

use crate::ast::{Expr, NamedExpression, OrderingSpec};
use crate::error::{KqlError, KqlResult};
use crate::evaluator::ExpressionEvaluator;
use crate::result::Row;
use crate::value::{
    compare_values, convert_to_double, convert_to_string, is_truthy_true, KqlValue,
};

pub trait KqlOperator {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>>;
}

pub struct WhereOp {
    predicate: Expr,
}

impl WhereOp {
    pub(crate) fn new(predicate: Expr) -> Self {
        Self { predicate }
    }
}

impl KqlOperator for WhereOp {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>> {
        input
            .into_iter()
            .map(|row| {
                let keep = matches!(
                    ExpressionEvaluator::evaluate(&self.predicate, &row)?,
                    KqlValue::Bool(true)
                );
                Ok(keep.then_some(row))
            })
            .filter_map(Result::transpose)
            .collect()
    }
}

pub struct ProjectOp {
    columns: Vec<NamedExpression>,
}

impl ProjectOp {
    pub(crate) fn new(columns: Vec<NamedExpression>) -> Self {
        Self { columns }
    }
}

impl KqlOperator for ProjectOp {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>> {
        input
            .iter()
            .map(|row| {
                let mut projected = Row::new();
                for column in &self.columns {
                    projected.insert(
                        column.name.clone(),
                        ExpressionEvaluator::evaluate_to_json(&column.expr, row)?,
                    );
                }
                Ok(projected)
            })
            .collect()
    }
}

pub struct ProjectAwayOp {
    columns: Vec<String>,
}

impl ProjectAwayOp {
    pub(crate) fn new(columns: Vec<String>) -> Self {
        Self { columns }
    }
}

impl KqlOperator for ProjectAwayOp {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>> {
        let remove: HashSet<String> = self
            .columns
            .iter()
            .map(|column| column.to_ascii_lowercase())
            .collect();
        Ok(input
            .into_iter()
            .map(|row| {
                row.into_iter()
                    .filter(|(key, _)| !remove.contains(&key.to_ascii_lowercase()))
                    .collect()
            })
            .collect())
    }
}

pub struct ExtendOp {
    columns: Vec<NamedExpression>,
}

impl ExtendOp {
    pub(crate) fn new(columns: Vec<NamedExpression>) -> Self {
        Self { columns }
    }
}

impl KqlOperator for ExtendOp {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>> {
        input
            .into_iter()
            .map(|row| {
                let mut extended = row.clone();
                for column in &self.columns {
                    extended.insert(
                        column.name.clone(),
                        ExpressionEvaluator::evaluate_to_json(&column.expr, &row)?,
                    );
                }
                Ok(extended)
            })
            .collect()
    }
}

pub struct SummarizeOp {
    aggregates: Vec<NamedExpression>,
    by_columns: Vec<NamedExpression>,
}

impl SummarizeOp {
    pub(crate) fn new(aggregates: Vec<NamedExpression>, by_columns: Vec<NamedExpression>) -> Self {
        Self {
            aggregates,
            by_columns,
        }
    }
}

impl KqlOperator for SummarizeOp {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>> {
        if self.by_columns.is_empty() {
            let mut result = Row::new();
            for aggregate in &self.aggregates {
                result.insert(
                    aggregate.name.clone(),
                    evaluate_aggregate(&input, &aggregate.expr)?.to_json(),
                );
            }
            return Ok(vec![result]);
        }

        let mut groups: Vec<(String, Row, Vec<Row>)> = Vec::new();
        for row in input {
            let mut key_values = Row::new();
            let mut key_parts = Vec::new();
            for by_column in &self.by_columns {
                let value = ExpressionEvaluator::evaluate(&by_column.expr, &row)?;
                key_parts.push(convert_to_string(&value).unwrap_or_else(|| "(null)".to_string()));
                key_values.insert(by_column.name.clone(), value.to_json());
            }
            let key = key_parts.join("|");
            if let Some((_, _, rows)) = groups
                .iter_mut()
                .find(|(group_key, _, _)| *group_key == key)
            {
                rows.push(row);
            } else {
                groups.push((key, key_values, vec![row]));
            }
        }

        groups
            .into_iter()
            .map(|(_, mut result, rows)| {
                for aggregate in &self.aggregates {
                    result.insert(
                        aggregate.name.clone(),
                        evaluate_aggregate(&rows, &aggregate.expr)?.to_json(),
                    );
                }
                Ok(result)
            })
            .collect()
    }
}

pub struct SortOp {
    orderings: Vec<OrderingSpec>,
}

impl SortOp {
    pub(crate) fn new(orderings: Vec<OrderingSpec>) -> Self {
        Self { orderings }
    }
}

impl KqlOperator for SortOp {
    fn execute(&self, mut input: Vec<Row>) -> KqlResult<Vec<Row>> {
        sort_rows(&mut input, &self.orderings);
        Ok(input)
    }
}

pub struct TopOp {
    count: i64,
    orderings: Vec<OrderingSpec>,
}

impl TopOp {
    pub(crate) fn new(count: i64, orderings: Vec<OrderingSpec>) -> Self {
        Self { count, orderings }
    }
}

impl KqlOperator for TopOp {
    fn execute(&self, mut input: Vec<Row>) -> KqlResult<Vec<Row>> {
        sort_rows(&mut input, &self.orderings);
        input.truncate(self.count.max(0) as usize);
        Ok(input)
    }
}

pub struct TakeOp {
    count: i64,
}

impl TakeOp {
    pub(crate) fn new(count: i64) -> Self {
        Self { count }
    }
}

impl KqlOperator for TakeOp {
    fn execute(&self, mut input: Vec<Row>) -> KqlResult<Vec<Row>> {
        input.truncate(self.count.max(0) as usize);
        Ok(input)
    }
}

pub struct CountOp;

impl KqlOperator for CountOp {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>> {
        let mut row = Row::new();
        row.insert("Count".to_string(), Value::from(input.len() as i64));
        Ok(vec![row])
    }
}

pub struct DistinctOp {
    columns: Vec<String>,
}

impl DistinctOp {
    pub(crate) fn new(columns: Vec<String>) -> Self {
        Self { columns }
    }
}

impl KqlOperator for DistinctOp {
    fn execute(&self, input: Vec<Row>) -> KqlResult<Vec<Row>> {
        let mut seen = HashSet::new();
        let mut output = Vec::new();
        for row in input {
            let key = if self.columns.is_empty() {
                String::new()
            } else {
                self.columns
                    .iter()
                    .map(|column| {
                        row.get(column)
                            .map(KqlValue::from)
                            .and_then(|value| convert_to_string(&value))
                            .unwrap_or_else(|| "(null)".to_string())
                    })
                    .collect::<Vec<_>>()
                    .join("|")
            };

            if seen.insert(key) {
                if self.columns.is_empty() {
                    output.push(row);
                } else {
                    let projected = self
                        .columns
                        .iter()
                        .map(|column| {
                            (
                                column.clone(),
                                row.get(column).cloned().unwrap_or(Value::Null),
                            )
                        })
                        .collect();
                    output.push(projected);
                }
            }
        }
        Ok(output)
    }
}

fn sort_rows(rows: &mut [Row], orderings: &[OrderingSpec]) {
    rows.sort_by(|a, b| {
        for ordering in orderings {
            let cmp = ExpressionEvaluator::compare_json_values(
                a.get(&ordering.column_name),
                b.get(&ordering.column_name),
            );
            if !cmp.is_eq() {
                return if ordering.ascending {
                    cmp
                } else {
                    cmp.reverse()
                };
            }
        }
        std::cmp::Ordering::Equal
    });
}

fn evaluate_aggregate(group: &[Row], agg_expr: &Expr) -> KqlResult<KqlValue> {
    if let Expr::Call { name, args } = agg_expr {
        let func_name = name.to_ascii_lowercase();
        match func_name.as_str() {
            "count" => Ok(KqlValue::Long(group.len() as i64)),
            "sum" => Ok(KqlValue::Real(sum(group, first_arg(args)?)?)),
            "avg" => {
                if group.is_empty() {
                    Ok(KqlValue::Null)
                } else {
                    Ok(KqlValue::Real(
                        sum(group, first_arg(args)?)? / group.len() as f64,
                    ))
                }
            }
            "min" => min_max(group, first_arg(args)?, false),
            "max" => min_max(group, first_arg(args)?, true),
            "dcount" | "count_distinct" => {
                let expr = first_arg(args)?;
                let mut set = HashSet::new();
                for row in group {
                    let value = ExpressionEvaluator::evaluate(expr, row)?;
                    set.insert(convert_to_string(&value));
                }
                Ok(KqlValue::Long(set.len() as i64))
            }
            "countif" => {
                let expr = first_arg(args)?;
                let mut count = 0;
                for row in group {
                    if is_truthy_true(&ExpressionEvaluator::evaluate(expr, row)?) {
                        count += 1;
                    }
                }
                Ok(KqlValue::Long(count))
            }
            "sumif" => {
                let value_expr = args
                    .first()
                    .ok_or_else(|| aggregate_arg_error("sumif", 0))?;
                let pred_expr = args.get(1).ok_or_else(|| aggregate_arg_error("sumif", 1))?;
                let mut total = 0.0;
                for row in group {
                    if is_truthy_true(&ExpressionEvaluator::evaluate(pred_expr, row)?) {
                        total +=
                            convert_to_double(&ExpressionEvaluator::evaluate(value_expr, row)?);
                    }
                }
                Ok(KqlValue::Real(total))
            }
            "avgif" => {
                let value_expr = args
                    .first()
                    .ok_or_else(|| aggregate_arg_error("avgif", 0))?;
                let pred_expr = args.get(1).ok_or_else(|| aggregate_arg_error("avgif", 1))?;
                let mut total = 0.0;
                let mut count = 0i64;
                for row in group {
                    if is_truthy_true(&ExpressionEvaluator::evaluate(pred_expr, row)?) {
                        total +=
                            convert_to_double(&ExpressionEvaluator::evaluate(value_expr, row)?);
                        count += 1;
                    }
                }
                if count == 0 {
                    Ok(KqlValue::Null)
                } else {
                    Ok(KqlValue::Real(total / count as f64))
                }
            }
            "make_list" => {
                let expr = first_arg(args)?;
                Ok(KqlValue::Array(
                    group
                        .iter()
                        .map(|row| ExpressionEvaluator::evaluate(expr, row))
                        .collect::<KqlResult<Vec<_>>>()?,
                ))
            }
            "make_set" => {
                let expr = first_arg(args)?;
                let mut seen = HashSet::new();
                let mut values = Vec::new();
                for row in group {
                    let value = ExpressionEvaluator::evaluate(expr, row)?;
                    if seen.insert(convert_to_string(&value)) {
                        values.push(value);
                    }
                }
                Ok(KqlValue::Array(values))
            }
            "any" | "take_any" => {
                let expr = first_arg(args)?;
                for row in group {
                    let value = ExpressionEvaluator::evaluate(expr, row)?;
                    if !matches!(value, KqlValue::Null) {
                        return Ok(value);
                    }
                }
                Ok(KqlValue::Null)
            }
            "percentile" => percentile(group, args),
            _ => Err(KqlError::Expression(format!(
                "Aggregate function '{func_name}' is not supported."
            ))),
        }
    } else if let Some(first) = group.first() {
        ExpressionEvaluator::evaluate(agg_expr, first)
    } else {
        Ok(KqlValue::Null)
    }
}

fn sum(group: &[Row], expr: &Expr) -> KqlResult<f64> {
    group.iter().try_fold(0.0, |total, row| {
        Ok(total + convert_to_double(&ExpressionEvaluator::evaluate(expr, row)?))
    })
}

fn min_max(group: &[Row], expr: &Expr, max: bool) -> KqlResult<KqlValue> {
    let mut selected: Option<KqlValue> = None;
    for row in group {
        let value = ExpressionEvaluator::evaluate(expr, row)?;
        if matches!(value, KqlValue::Null) {
            continue;
        }
        let replace = selected.as_ref().is_none_or(|current| {
            let cmp = compare_values(&value, current);
            if max {
                cmp.is_gt()
            } else {
                cmp.is_lt()
            }
        });
        if replace {
            selected = Some(value);
        }
    }
    Ok(selected.unwrap_or(KqlValue::Null))
}

fn percentile(group: &[Row], args: &[Expr]) -> KqlResult<KqlValue> {
    let value_expr = args
        .first()
        .ok_or_else(|| aggregate_arg_error("percentile", 0))?;
    let percent_expr = args
        .get(1)
        .ok_or_else(|| aggregate_arg_error("percentile", 1))?;
    let mut values = group
        .iter()
        .map(|row| {
            Ok(convert_to_double(&ExpressionEvaluator::evaluate(
                value_expr, row,
            )?))
        })
        .collect::<KqlResult<Vec<_>>>()?;
    values.sort_by(|left, right| left.partial_cmp(right).unwrap_or(std::cmp::Ordering::Equal));
    if values.is_empty() {
        return Ok(KqlValue::Null);
    }
    let percent = ExpressionEvaluator::evaluate(percent_expr, &group[0])
        .map(|value| convert_to_double(&value))?;
    let n = (percent / 100.0) * (values.len() - 1) as f64;
    let lower = n.floor() as usize;
    let upper = n.ceil() as usize;
    if lower == upper || upper >= values.len() {
        Ok(KqlValue::Real(values[lower]))
    } else {
        Ok(KqlValue::Real(
            values[lower] + (n - lower as f64) * (values[upper] - values[lower]),
        ))
    }
}

fn first_arg(args: &[Expr]) -> KqlResult<&Expr> {
    args.first()
        .ok_or_else(|| aggregate_arg_error("aggregate", 0))
}

fn aggregate_arg_error(function: &str, index: usize) -> KqlError {
    KqlError::Expression(format!(
        "Aggregate function '{function}' is missing argument {index}."
    ))
}
