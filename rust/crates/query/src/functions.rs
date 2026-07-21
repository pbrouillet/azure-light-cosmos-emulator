//! Built-in scalar functions for the Cosmos SQL subset.
//!
//! Ports a commonly-used subset of `CosmosQueryEngine.EvaluateBuiltInFunction`
//! (string, type-check, math, array, and conditional functions). Aggregates are
//! handled separately in the executor.

use serde_json::Value;

use crate::value::{as_f64, number, QVal};

fn as_str(v: &QVal) -> Option<&str> {
    match v {
        Some(Value::String(s)) => Some(s.as_str()),
        _ => None,
    }
}

/// Dispatches a built-in scalar function by (upper-cased) name.
/// Returns `Err` for unknown functions.
pub fn call(name: &str, args: &[QVal]) -> Result<QVal, String> {
    let upper = name.to_ascii_uppercase();
    let arg = |i: usize| args.get(i).cloned().unwrap_or(None);
    let result = match upper.as_str() {
        // ---- string ----
        "CONCAT" => {
            let mut s = String::new();
            for a in args {
                match a {
                    None => return Ok(None),
                    Some(v) => s.push_str(&stringify(v)),
                }
            }
            Some(Value::String(s))
        }
        "CONTAINS" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::Bool(h.contains(n))),
            _ => None,
        },
        "STARTSWITH" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::Bool(h.starts_with(n))),
            _ => None,
        },
        "ENDSWITH" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::Bool(h.ends_with(n))),
            _ => None,
        },
        "UPPER" => as_str(&arg(0)).map(|s| Value::String(s.to_uppercase())),
        "LOWER" => as_str(&arg(0)).map(|s| Value::String(s.to_lowercase())),
        "TRIM" => as_str(&arg(0)).map(|s| Value::String(s.trim().to_string())),
        "LTRIM" => as_str(&arg(0)).map(|s| Value::String(s.trim_start().to_string())),
        "RTRIM" => as_str(&arg(0)).map(|s| Value::String(s.trim_end().to_string())),
        "REVERSE" => as_str(&arg(0)).map(|s| Value::String(s.chars().rev().collect())),
        "LENGTH" => as_str(&arg(0)).map(|s| Value::from(s.chars().count() as i64)),
        "REPLACE" => match (as_str(&arg(0)), as_str(&arg(1)), as_str(&arg(2))) {
            (Some(s), Some(old), Some(new)) => Some(Value::String(s.replace(old, new))),
            _ => None,
        },
        "SUBSTRING" => substring(&arg(0), &arg(1), args.get(2).cloned().flatten()),
        "INDEX_OF" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(h), Some(n)) => Some(Value::from(
                h.find(n)
                    .map(|b| h[..b].chars().count() as i64)
                    .unwrap_or(-1),
            )),
            _ => None,
        },
        "STRINGEQUALS" => match (as_str(&arg(0)), as_str(&arg(1))) {
            (Some(a), Some(b)) => Some(Value::Bool(a == b)),
            _ => None,
        },
        "TOSTRING" => arg(0).map(|v| Value::String(stringify(&v))),
        // ---- type checks ----
        "IS_DEFINED" => Some(Value::Bool(arg(0).is_some())),
        "IS_NULL" => Some(Value::Bool(matches!(arg(0), Some(Value::Null)))),
        "IS_STRING" => Some(Value::Bool(matches!(arg(0), Some(Value::String(_))))),
        "IS_NUMBER" => Some(Value::Bool(matches!(arg(0), Some(Value::Number(_))))),
        "IS_BOOL" => Some(Value::Bool(matches!(arg(0), Some(Value::Bool(_))))),
        "IS_ARRAY" => Some(Value::Bool(matches!(arg(0), Some(Value::Array(_))))),
        "IS_OBJECT" => Some(Value::Bool(matches!(arg(0), Some(Value::Object(_))))),
        "IS_PRIMITIVE" => Some(Value::Bool(matches!(
            arg(0),
            Some(Value::Null | Value::Bool(_) | Value::Number(_) | Value::String(_))
        ))),
        // ---- math ----
        "ABS" => unary_num(&arg(0), f64::abs),
        "CEILING" => unary_num(&arg(0), f64::ceil),
        "FLOOR" => unary_num(&arg(0), f64::floor),
        "ROUND" => unary_num(&arg(0), |v| v.round()),
        "TRUNC" => unary_num(&arg(0), f64::trunc),
        "SIGN" => unary_num(&arg(0), |v| v.signum().trunc() * (v != 0.0) as i32 as f64),
        "SQRT" => unary_num(&arg(0), f64::sqrt),
        "EXP" => unary_num(&arg(0), f64::exp),
        "LOG" => unary_num(&arg(0), f64::ln),
        "LOG10" => unary_num(&arg(0), f64::log10),
        "SIN" => unary_num(&arg(0), f64::sin),
        "COS" => unary_num(&arg(0), f64::cos),
        "TAN" => unary_num(&arg(0), f64::tan),
        "PI" => number(std::f64::consts::PI),
        "POWER" => match (as_f64(&arg(0)), as_f64(&arg(1))) {
            (Some(a), Some(b)) => number(a.powf(b)),
            _ => None,
        },
        "SQUARE" => unary_num(&arg(0), |v| v * v),
        "DEGREES" => unary_num(&arg(0), |v| v.to_degrees()),
        "RADIANS" => unary_num(&arg(0), |v| v.to_radians()),
        // ---- array ----
        "ARRAY_LENGTH" => match arg(0) {
            Some(Value::Array(a)) => Some(Value::from(a.len() as i64)),
            _ => None,
        },
        "ARRAY_CONTAINS" => match arg(0) {
            Some(Value::Array(a)) => {
                let needle = arg(1);
                let partial = matches!(arg(2), Some(Value::Bool(true)));
                Some(Value::Bool(array_contains(&a, &needle, partial)))
            }
            _ => None,
        },
        "ARRAY_CONCAT" => {
            let mut out = Vec::new();
            for a in args {
                match a {
                    Some(Value::Array(arr)) => out.extend(arr.iter().cloned()),
                    _ => return Ok(None),
                }
            }
            Some(Value::Array(out))
        }
        "ARRAY_SLICE" => array_slice(&arg(0), &arg(1), args.get(2).cloned().flatten()),
        // ---- conditional ----
        "IIF" => {
            if matches!(arg(0), Some(Value::Bool(true))) {
                arg(1)
            } else {
                arg(2)
            }
        }
        _ => return Err(format!("Unknown function: {name}")),
    };
    Ok(result)
}

/// Returns `true` if `name` is a recognized aggregate function.
pub fn is_aggregate(name: &str) -> bool {
    matches!(
        name.to_ascii_uppercase().as_str(),
        "COUNT" | "SUM" | "AVG" | "MIN" | "MAX"
    )
}

fn stringify(v: &Value) -> String {
    match v {
        Value::String(s) => s.clone(),
        Value::Null => "null".to_string(),
        Value::Bool(b) => b.to_string(),
        Value::Number(n) => n.to_string(),
        other => other.to_string(),
    }
}

fn unary_num(v: &QVal, f: impl Fn(f64) -> f64) -> QVal {
    as_f64(v).and_then(|n| number(f(n)))
}

fn substring(s: &QVal, start: &QVal, length: Option<Value>) -> QVal {
    let s = as_str(s)?;
    let chars: Vec<char> = s.chars().collect();
    let start = as_f64(start)? as i64;
    if start < 0 {
        return Some(Value::String(String::new()));
    }
    let start = start as usize;
    if start >= chars.len() {
        return Some(Value::String(String::new()));
    }
    let end = match length {
        Some(Value::Number(n)) => {
            let len = n.as_f64().unwrap_or(0.0).max(0.0) as usize;
            (start + len).min(chars.len())
        }
        _ => chars.len(),
    };
    Some(Value::String(chars[start..end].iter().collect()))
}

fn array_contains(arr: &[Value], needle: &QVal, partial: bool) -> bool {
    let Some(needle) = needle else {
        return false;
    };
    if partial {
        if let Value::Object(target) = needle {
            return arr.iter().any(|item| {
                if let Value::Object(obj) = item {
                    target.iter().all(|(k, v)| obj.get(k) == Some(v))
                } else {
                    false
                }
            });
        }
    }
    arr.iter().any(|item| item == needle)
}

fn array_slice(arr: &QVal, start: &QVal, count: Option<Value>) -> QVal {
    let Some(Value::Array(a)) = arr else {
        return None;
    };
    let len = a.len() as i64;
    let mut start = as_f64(start)? as i64;
    if start < 0 {
        start = (len + start).max(0);
    }
    let start = start.min(len).max(0) as usize;
    let end = match count {
        Some(Value::Number(n)) => {
            let c = n.as_f64().unwrap_or(0.0).max(0.0) as usize;
            (start + c).min(a.len())
        }
        _ => a.len(),
    };
    Some(Value::Array(a[start..end.max(start)].to_vec()))
}
