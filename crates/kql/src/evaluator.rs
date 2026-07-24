use chrono::{DateTime, Duration, Utc};
use serde_json::Value;
use std::cmp::Ordering;

use crate::ast::{BinaryOp, Expr, UnaryOp};
use crate::error::{KqlError, KqlResult};
use crate::result::Row;
use crate::value::{
    compare_values, convert_to_bool, convert_to_datetime, convert_to_double, convert_to_long,
    convert_to_string, object_equals, row_get, KqlValue,
};

pub struct ExpressionEvaluator;

impl ExpressionEvaluator {
    pub(crate) fn evaluate(expr: &Expr, row: &Row) -> KqlResult<KqlValue> {
        match expr {
            Expr::Literal(value) => Ok(value.clone()),
            Expr::Identifier(name) => Ok(row_get(row, name)),
            Expr::Member(base, member) => match Self::evaluate(base, row)? {
                KqlValue::Object(values) => {
                    Ok(values.get(member).cloned().unwrap_or(KqlValue::Null))
                }
                other => {
                    let dotted = format!(
                        "{}.{}",
                        convert_to_string(&other).unwrap_or_default(),
                        member
                    );
                    Ok(row_get(row, &dotted))
                }
            },
            Expr::Index(base, index) => Self::evaluate_index(base, index, row),
            Expr::Unary(op, expr) => Self::evaluate_unary(*op, expr, row),
            Expr::Binary(op, left, right) => Self::evaluate_binary(*op, left, right, row),
            Expr::In {
                expr,
                values,
                negated,
            } => {
                let left = Self::evaluate(expr, row)?;
                let is_in = values
                    .iter()
                    .map(|value| Self::evaluate(value, row))
                    .collect::<KqlResult<Vec<_>>>()?
                    .iter()
                    .any(|value| object_equals(&left, value));
                Ok(KqlValue::Bool(if *negated { !is_in } else { is_in }))
            }
            Expr::Call { name, args } => Self::evaluate_function(name, args, row),
        }
    }

    pub(crate) fn evaluate_to_json(expr: &Expr, row: &Row) -> KqlResult<Value> {
        Ok(Self::evaluate(expr, row)?.to_json())
    }

    pub fn convert_to_string(value: &Value) -> Option<String> {
        convert_to_string(&KqlValue::from(value))
    }

    pub fn convert_to_long(value: &Value) -> i64 {
        convert_to_long(&KqlValue::from(value))
    }

    pub fn convert_to_double(value: &Value) -> f64 {
        convert_to_double(&KqlValue::from(value))
    }

    pub fn compare_json_values(a: Option<&Value>, b: Option<&Value>) -> Ordering {
        let a = a.map(KqlValue::from).unwrap_or(KqlValue::Null);
        let b = b.map(KqlValue::from).unwrap_or(KqlValue::Null);
        compare_values(&a, &b)
    }

    fn evaluate_index(base: &Expr, index: &Expr, row: &Row) -> KqlResult<KqlValue> {
        let base = Self::evaluate(base, row)?;
        let index = Self::evaluate(index, row)?;
        match (base, index) {
            (KqlValue::Array(values), index) => Ok(values
                .get(convert_to_long(&index) as usize)
                .cloned()
                .unwrap_or(KqlValue::Null)),
            (KqlValue::Object(values), KqlValue::String(key)) => {
                Ok(values.get(&key).cloned().unwrap_or(KqlValue::Null))
            }
            _ => Ok(KqlValue::Null),
        }
    }

    fn evaluate_unary(op: UnaryOp, expr: &Expr, row: &Row) -> KqlResult<KqlValue> {
        let value = Self::evaluate(expr, row)?;
        Ok(match op {
            UnaryOp::Not => KqlValue::Bool(!(convert_to_bool(&value).unwrap_or(false))),
            UnaryOp::Neg => negate(&value),
            UnaryOp::Pos => value,
        })
    }

    fn evaluate_binary(op: BinaryOp, left: &Expr, right: &Expr, row: &Row) -> KqlResult<KqlValue> {
        let left = Self::evaluate(left, row)?;
        let right = Self::evaluate(right, row)?;
        Ok(match op {
            BinaryOp::Eq => KqlValue::Bool(object_equals(&left, &right)),
            BinaryOp::Ne => KqlValue::Bool(!object_equals(&left, &right)),
            BinaryOp::Lt => KqlValue::Bool(compare_values(&left, &right) == Ordering::Less),
            BinaryOp::Le => KqlValue::Bool(matches!(
                compare_values(&left, &right),
                Ordering::Less | Ordering::Equal
            )),
            BinaryOp::Gt => KqlValue::Bool(compare_values(&left, &right) == Ordering::Greater),
            BinaryOp::Ge => KqlValue::Bool(matches!(
                compare_values(&left, &right),
                Ordering::Greater | Ordering::Equal
            )),
            BinaryOp::Add => add(&left, &right),
            BinaryOp::Sub => subtract(&left, &right),
            BinaryOp::Mul => multiply(&left, &right),
            BinaryOp::Div => divide(&left, &right),
            BinaryOp::Mod => modulo(&left, &right),
            BinaryOp::And => KqlValue::Bool(
                convert_to_bool(&left) == Some(true) && convert_to_bool(&right) == Some(true),
            ),
            BinaryOp::Or => KqlValue::Bool(
                convert_to_bool(&left) == Some(true) || convert_to_bool(&right) == Some(true),
            ),
            BinaryOp::Has(ignore_case) | BinaryOp::Contains(ignore_case) => {
                KqlValue::Bool(string_contains(&left, &right, ignore_case))
            }
            BinaryOp::StartsWith(ignore_case) => {
                KqlValue::Bool(string_starts_with(&left, &right, ignore_case))
            }
            BinaryOp::EndsWith(ignore_case) => {
                KqlValue::Bool(string_ends_with(&left, &right, ignore_case))
            }
        })
    }

    fn evaluate_function(name: &str, args: &[Expr], row: &Row) -> KqlResult<KqlValue> {
        let func_name = name.to_ascii_lowercase();
        if func_name == "__array" {
            return Ok(KqlValue::Array(
                args.iter()
                    .map(|arg| Self::evaluate(arg, row))
                    .collect::<KqlResult<Vec<_>>>()?,
            ));
        }

        let eval_arg = |index: usize| -> KqlResult<KqlValue> {
            args.get(index)
                .map(|arg| Self::evaluate(arg, row))
                .unwrap_or(Ok(KqlValue::Null))
        };

        match func_name.as_str() {
            "now" => Ok(KqlValue::DateTime(Utc::now())),
            "ago" => {
                let duration = match eval_arg(0)? {
                    KqlValue::Duration(value) => value,
                    _ => Duration::zero(),
                };
                Ok(KqlValue::DateTime(Utc::now() - duration))
            }
            "strlen" => Ok(KqlValue::Long(
                convert_to_string(&eval_arg(0)?)
                    .map(|value| value.chars().count() as i64)
                    .unwrap_or(0),
            )),
            "toupper" => Ok(nullable_string(eval_arg(0)?, |value| value.to_uppercase())),
            "tolower" => Ok(nullable_string(eval_arg(0)?, |value| value.to_lowercase())),
            "trim" => {
                let value = if args.len() >= 2 {
                    eval_arg(1)?
                } else {
                    eval_arg(0)?
                };
                Ok(nullable_string(value, |value| value.trim().to_string()))
            }
            "substring" => {
                let value = convert_to_string(&eval_arg(0)?).unwrap_or_default();
                let chars: Vec<char> = value.chars().collect();
                let start = convert_to_long(&eval_arg(1)?).clamp(0, chars.len() as i64) as usize;
                let default_len = chars.len().saturating_sub(start) as i64;
                let len = if args.len() > 2 {
                    convert_to_long(&eval_arg(2)?)
                } else {
                    default_len
                }
                .clamp(0, default_len) as usize;
                Ok(KqlValue::String(chars[start..start + len].iter().collect()))
            }
            "strcat" => {
                let mut output = String::new();
                for arg in args {
                    output.push_str(
                        &convert_to_string(&Self::evaluate(arg, row)?).unwrap_or_default(),
                    );
                }
                Ok(KqlValue::String(output))
            }
            "tostring" => Ok(convert_to_string(&eval_arg(0)?)
                .map(KqlValue::String)
                .unwrap_or(KqlValue::Null)),
            "toint" | "tolong" => Ok(KqlValue::Long(convert_to_long(&eval_arg(0)?))),
            "todouble" | "toreal" => Ok(KqlValue::Real(convert_to_double(&eval_arg(0)?))),
            "todatetime" | "datetime" => Ok(convert_to_datetime(&eval_arg(0)?)
                .map(KqlValue::DateTime)
                .unwrap_or(KqlValue::Null)),
            "timespan" => Ok(parse_timespan_function(&eval_arg(0)?)),
            "isnull" | "isempty" => {
                let value = eval_arg(0)?;
                let result = matches!(value, KqlValue::Null)
                    || matches!(&value, KqlValue::String(text) if text.is_empty());
                Ok(KqlValue::Bool(result))
            }
            "isnotnull" | "isnotempty" => {
                let value = eval_arg(0)?;
                let result = !matches!(value, KqlValue::Null)
                    && !matches!(&value, KqlValue::String(text) if text.is_empty());
                Ok(KqlValue::Bool(result))
            }
            "iff" | "iif" => {
                if convert_to_bool(&eval_arg(0)?) == Some(true) {
                    eval_arg(1)
                } else {
                    eval_arg(2)
                }
            }
            "coalesce" => {
                for arg in args {
                    let value = Self::evaluate(arg, row)?;
                    if !matches!(value, KqlValue::Null) {
                        return Ok(value);
                    }
                }
                Ok(KqlValue::Null)
            }
            "not" => Ok(KqlValue::Bool(
                !convert_to_bool(&eval_arg(0)?).unwrap_or(false),
            )),
            "bin" | "floor" => {
                let value = eval_arg(0)?;
                let round_to = if args.len() > 1 {
                    eval_arg(1)?
                } else {
                    KqlValue::Long(1)
                };
                if let (KqlValue::DateTime(dt), KqlValue::Duration(duration)) = (&value, &round_to)
                {
                    let millis = duration.num_milliseconds();
                    if millis == 0 {
                        return Ok(value);
                    }
                    let ts = dt.timestamp_millis();
                    return DateTime::from_timestamp_millis(ts - ts.rem_euclid(millis))
                        .map(KqlValue::DateTime)
                        .ok_or_else(|| {
                            KqlError::Expression("datetime bin result is out of range".into())
                        });
                }
                let num_value = convert_to_double(&value);
                let num_round = convert_to_double(&round_to);
                if num_round == 0.0 {
                    Ok(KqlValue::Real(num_value))
                } else {
                    Ok(KqlValue::Real((num_value / num_round).floor() * num_round))
                }
            }
            "round" => {
                let value = convert_to_double(&eval_arg(0)?);
                let precision = if args.len() > 1 {
                    convert_to_long(&eval_arg(1)?) as i32
                } else {
                    0
                };
                let scale = 10_f64.powi(precision);
                Ok(KqlValue::Real((value * scale).round() / scale))
            }
            "format_datetime" => {
                let datetime = convert_to_datetime(&eval_arg(0)?);
                let format = convert_to_string(&eval_arg(1)?)
                    .unwrap_or_else(|| "yyyy-MM-dd HH:mm:ss".to_string());
                Ok(datetime
                    .map(|dt| KqlValue::String(format_datetime(dt, &format)))
                    .unwrap_or(KqlValue::Null))
            }
            "datetime_diff" => {
                let part = convert_to_string(&eval_arg(0)?)
                    .unwrap_or_else(|| "second".to_string())
                    .to_ascii_lowercase();
                let dt1 = convert_to_datetime(&eval_arg(1)?);
                let dt2 = convert_to_datetime(&eval_arg(2)?);
                match (dt1, dt2) {
                    (Some(dt1), Some(dt2)) => {
                        let diff = dt1 - dt2;
                        let value = match part.as_str() {
                            "second" => diff.num_seconds(),
                            "minute" => diff.num_minutes(),
                            "hour" => diff.num_hours(),
                            "day" => diff.num_days(),
                            _ => diff.num_seconds(),
                        };
                        Ok(KqlValue::Long(value))
                    }
                    _ => Ok(KqlValue::Null),
                }
            }
            other => Err(KqlError::Expression(format!(
                "Function '{other}' is not supported."
            ))),
        }
    }
}

fn nullable_string(value: KqlValue, transform: impl FnOnce(&str) -> String) -> KqlValue {
    convert_to_string(&value)
        .map(|value| KqlValue::String(transform(&value)))
        .unwrap_or(KqlValue::Null)
}

fn negate(value: &KqlValue) -> KqlValue {
    match value {
        KqlValue::Long(value) => KqlValue::Long(-value),
        KqlValue::Real(value) => KqlValue::Real(-value),
        _ => KqlValue::Real(-convert_to_double(value)),
    }
}

fn add(left: &KqlValue, right: &KqlValue) -> KqlValue {
    if matches!(left, KqlValue::String(_)) || matches!(right, KqlValue::String(_)) {
        return KqlValue::String(
            (convert_to_string(left).unwrap_or_default()
                + &convert_to_string(right).unwrap_or_default())
                .to_string(),
        );
    }
    if let (KqlValue::DateTime(dt), KqlValue::Duration(duration)) = (left, right) {
        return KqlValue::DateTime(*dt + *duration);
    }
    if let (KqlValue::Long(left), KqlValue::Long(right)) = (left, right) {
        return KqlValue::Long(left + right);
    }
    KqlValue::Real(convert_to_double(left) + convert_to_double(right))
}

fn subtract(left: &KqlValue, right: &KqlValue) -> KqlValue {
    match (left, right) {
        (KqlValue::DateTime(left), KqlValue::DateTime(right)) => KqlValue::Duration(*left - *right),
        (KqlValue::DateTime(left), KqlValue::Duration(right)) => KqlValue::DateTime(*left - *right),
        (KqlValue::Long(left), KqlValue::Long(right)) => KqlValue::Long(left - right),
        _ => KqlValue::Real(convert_to_double(left) - convert_to_double(right)),
    }
}

fn multiply(left: &KqlValue, right: &KqlValue) -> KqlValue {
    if let (KqlValue::Long(left), KqlValue::Long(right)) = (left, right) {
        KqlValue::Long(left * right)
    } else {
        KqlValue::Real(convert_to_double(left) * convert_to_double(right))
    }
}

fn divide(left: &KqlValue, right: &KqlValue) -> KqlValue {
    let divisor = convert_to_double(right);
    if divisor == 0.0 {
        KqlValue::Null
    } else {
        KqlValue::Real(convert_to_double(left) / divisor)
    }
}

fn modulo(left: &KqlValue, right: &KqlValue) -> KqlValue {
    if let (KqlValue::Long(left), KqlValue::Long(right)) = (left, right) {
        return if *right == 0 {
            KqlValue::Null
        } else {
            KqlValue::Long(left % right)
        };
    }
    let divisor = convert_to_double(right);
    if divisor == 0.0 {
        KqlValue::Null
    } else {
        KqlValue::Real(convert_to_double(left) % divisor)
    }
}

fn string_contains(left: &KqlValue, right: &KqlValue, ignore_case: bool) -> bool {
    let Some(left) = convert_to_string(left) else {
        return false;
    };
    let Some(right) = convert_to_string(right) else {
        return false;
    };
    if ignore_case {
        left.to_lowercase().contains(&right.to_lowercase())
    } else {
        left.contains(&right)
    }
}

fn string_starts_with(left: &KqlValue, right: &KqlValue, ignore_case: bool) -> bool {
    let Some(left) = convert_to_string(left) else {
        return false;
    };
    let Some(right) = convert_to_string(right) else {
        return false;
    };
    if ignore_case {
        left.to_lowercase().starts_with(&right.to_lowercase())
    } else {
        left.starts_with(&right)
    }
}

fn string_ends_with(left: &KqlValue, right: &KqlValue, ignore_case: bool) -> bool {
    let Some(left) = convert_to_string(left) else {
        return false;
    };
    let Some(right) = convert_to_string(right) else {
        return false;
    };
    if ignore_case {
        left.to_lowercase().ends_with(&right.to_lowercase())
    } else {
        left.ends_with(&right)
    }
}

fn parse_timespan_function(value: &KqlValue) -> KqlValue {
    let Some(text) = convert_to_string(value) else {
        return KqlValue::Null;
    };
    let Some((number, unit)) = split_number_unit(&text) else {
        return KqlValue::Null;
    };
    let Ok(number) = number.parse::<f64>() else {
        return KqlValue::Null;
    };
    let millis = match unit.to_ascii_lowercase().as_str() {
        "ms" => number,
        "s" => number * 1_000.0,
        "m" => number * 60_000.0,
        "h" => number * 3_600_000.0,
        "d" => number * 86_400_000.0,
        _ => return KqlValue::Null,
    };
    KqlValue::Duration(Duration::milliseconds(millis as i64))
}

fn split_number_unit(input: &str) -> Option<(&str, &str)> {
    let idx = input
        .char_indices()
        .find_map(|(idx, ch)| (!ch.is_ascii_digit() && ch != '.').then_some(idx))?;
    Some((&input[..idx], &input[idx..]))
}

fn format_datetime(datetime: DateTime<Utc>, format: &str) -> String {
    let chrono_format = format
        .replace("yyyy", "%Y")
        .replace("MM", "%m")
        .replace("dd", "%d")
        .replace("HH", "%H")
        .replace("mm", "%M")
        .replace("ss", "%S");
    datetime.format(&chrono_format).to_string()
}
