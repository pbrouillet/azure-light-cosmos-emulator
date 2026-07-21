//! Runtime value semantics for Cosmos SQL evaluation.
//!
//! Values are represented as `Option<Value>` where `None` models the Cosmos
//! `Undefined` value (an absent property), distinct from JSON `null`.

use std::cmp::Ordering;

use serde_json::Value;

/// A runtime value: `None` is Cosmos `Undefined`; `Some(v)` a concrete JSON value.
pub type QVal = Option<Value>;

/// Type rank used for the Cosmos total ordering (Undefined < Null < Bool <
/// Number < String < Array < Object).
fn type_rank(v: &QVal) -> u8 {
    match v {
        None => 0,
        Some(Value::Null) => 1,
        Some(Value::Bool(_)) => 2,
        Some(Value::Number(_)) => 3,
        Some(Value::String(_)) => 4,
        Some(Value::Array(_)) => 5,
        Some(Value::Object(_)) => 6,
    }
}

/// Coerces a value to `f64` when it is a JSON number.
pub fn as_f64(v: &QVal) -> Option<f64> {
    match v {
        Some(Value::Number(n)) => n.as_f64(),
        _ => None,
    }
}

/// Truthiness for predicate contexts: only the boolean `true` is truthy.
pub fn is_true(v: &QVal) -> bool {
    matches!(v, Some(Value::Bool(true)))
}

/// Wraps an `f64` result as a JSON number value, normalizing integers.
pub fn number(n: f64) -> QVal {
    if n.is_finite() {
        if n.fract() == 0.0 && n.abs() < 9.007_199_254_740_992e15 {
            Some(Value::from(n as i64))
        } else {
            serde_json::Number::from_f64(n).map(Value::Number)
        }
    } else {
        // NaN / Infinity are Undefined in Cosmos numeric semantics.
        None
    }
}

/// Equality per Cosmos semantics. Returns `None` (undefined) when either side
/// is undefined; otherwise a concrete boolean.
pub fn equals(a: &QVal, b: &QVal) -> Option<bool> {
    match (a, b) {
        (None, _) | (_, None) => None,
        (Some(x), Some(y)) => Some(json_equal(x, y)),
    }
}

fn json_equal(x: &Value, y: &Value) -> bool {
    match (x, y) {
        (Value::Number(a), Value::Number(b)) => match (a.as_f64(), b.as_f64()) {
            (Some(av), Some(bv)) => av == bv,
            _ => false,
        },
        _ => x == y,
    }
}

/// Ordered comparison for relational operators (`<`, `<=`, `>`, `>=`).
/// Returns `None` when either side is undefined or the operands are not
/// comparable (different primitive types).
pub fn compare_relational(a: &QVal, b: &QVal) -> Option<Ordering> {
    match (a, b) {
        (None, _) | (_, None) => None,
        (Some(x), Some(y)) => primitive_cmp(x, y),
    }
}

fn primitive_cmp(x: &Value, y: &Value) -> Option<Ordering> {
    match (x, y) {
        (Value::Number(a), Value::Number(b)) => a.as_f64()?.partial_cmp(&b.as_f64()?),
        (Value::String(a), Value::String(b)) => Some(a.cmp(b)),
        (Value::Bool(a), Value::Bool(b)) => Some(a.cmp(b)),
        (Value::Null, Value::Null) => Some(Ordering::Equal),
        _ => None,
    }
}

/// Total ordering used for `ORDER BY`. Applies the Cosmos type ordering, then
/// orders within a type. Undefined sorts first.
pub fn total_cmp(a: &QVal, b: &QVal) -> Ordering {
    let (ra, rb) = (type_rank(a), type_rank(b));
    if ra != rb {
        return ra.cmp(&rb);
    }
    match (a, b) {
        (Some(Value::Number(x)), Some(Value::Number(y))) => x
            .as_f64()
            .unwrap_or(0.0)
            .partial_cmp(&y.as_f64().unwrap_or(0.0))
            .unwrap_or(Ordering::Equal),
        (Some(Value::String(x)), Some(Value::String(y))) => x.cmp(y),
        (Some(Value::Bool(x)), Some(Value::Bool(y))) => x.cmp(y),
        _ => Ordering::Equal,
    }
}
