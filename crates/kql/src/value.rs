use std::cmp::Ordering;
use std::collections::BTreeMap;

use chrono::{DateTime, Duration, SecondsFormat, Utc};
use serde_json::{Map, Number, Value};

#[derive(Debug, Clone)]
pub(crate) enum KqlValue {
    Null,
    Bool(bool),
    Long(i64),
    Real(f64),
    String(String),
    DateTime(DateTime<Utc>),
    Duration(Duration),
    Array(Vec<KqlValue>),
    Object(BTreeMap<String, KqlValue>),
}

impl KqlValue {
    pub(crate) fn to_json(&self) -> Value {
        match self {
            Self::Null => Value::Null,
            Self::Bool(v) => Value::Bool(*v),
            Self::Long(v) => Value::from(*v),
            Self::Real(v) => Number::from_f64(*v).map_or(Value::Null, Value::Number),
            Self::String(v) => Value::String(v.clone()),
            Self::DateTime(v) => Value::String(v.to_rfc3339_opts(SecondsFormat::Millis, true)),
            Self::Duration(v) => Value::String(format_duration(*v)),
            Self::Array(values) => Value::Array(values.iter().map(Self::to_json).collect()),
            Self::Object(values) => Value::Object(
                values
                    .iter()
                    .map(|(key, value)| (key.clone(), value.to_json()))
                    .collect(),
            ),
        }
    }
}

impl From<&Value> for KqlValue {
    fn from(value: &Value) -> Self {
        match value {
            Value::Null => Self::Null,
            Value::Bool(v) => Self::Bool(*v),
            Value::Number(n) => n
                .as_i64()
                .map(Self::Long)
                .or_else(|| {
                    n.as_u64()
                        .and_then(|v| i64::try_from(v).ok())
                        .map(Self::Long)
                })
                .unwrap_or_else(|| Self::Real(n.as_f64().unwrap_or(0.0))),
            Value::String(v) => Self::String(v.clone()),
            Value::Array(values) => Self::Array(values.iter().map(Self::from).collect()),
            Value::Object(values) => Self::Object(
                values
                    .iter()
                    .map(|(key, value)| (key.clone(), Self::from(value)))
                    .collect(),
            ),
        }
    }
}

pub(crate) fn object_equals(a: &KqlValue, b: &KqlValue) -> bool {
    match (a, b) {
        (KqlValue::Null, KqlValue::Null) => true,
        (KqlValue::Null, _) | (_, KqlValue::Null) => false,
        _ if is_numeric(a) && is_numeric(b) => convert_to_double(a) == convert_to_double(b),
        (KqlValue::String(a), KqlValue::String(b)) => a.eq_ignore_ascii_case(b),
        (KqlValue::Bool(a), KqlValue::Bool(b)) => a == b,
        (KqlValue::DateTime(a), KqlValue::DateTime(b)) => a == b,
        (KqlValue::Duration(a), KqlValue::Duration(b)) => a == b,
        _ => a.to_json() == b.to_json(),
    }
}

pub(crate) fn compare_values(a: &KqlValue, b: &KqlValue) -> Ordering {
    match (a, b) {
        (KqlValue::Null, KqlValue::Null) => Ordering::Equal,
        (KqlValue::Null, _) => Ordering::Less,
        (_, KqlValue::Null) => Ordering::Greater,
        (KqlValue::DateTime(a), _) => convert_to_datetime(b).map_or(Ordering::Equal, |b| a.cmp(&b)),
        _ if is_numeric(a) || is_numeric(b) => convert_to_double(a)
            .partial_cmp(&convert_to_double(b))
            .unwrap_or(Ordering::Equal),
        _ => convert_to_string(a)
            .unwrap_or_default()
            .to_ascii_lowercase()
            .cmp(
                &convert_to_string(b)
                    .unwrap_or_default()
                    .to_ascii_lowercase(),
            ),
    }
}

pub(crate) fn convert_to_string(value: &KqlValue) -> Option<String> {
    match value {
        KqlValue::Null => None,
        KqlValue::String(v) => Some(v.clone()),
        KqlValue::DateTime(v) => Some(v.to_rfc3339_opts(SecondsFormat::Millis, true)),
        KqlValue::Duration(v) => Some(format_duration(*v)),
        KqlValue::Bool(v) => Some(v.to_string()),
        KqlValue::Long(v) => Some(v.to_string()),
        KqlValue::Real(v) => Some(format_real(*v)),
        KqlValue::Array(_) | KqlValue::Object(_) => Some(value.to_json().to_string()),
    }
}

pub(crate) fn convert_to_double(value: &KqlValue) -> f64 {
    match value {
        KqlValue::Long(v) => *v as f64,
        KqlValue::Real(v) => *v,
        KqlValue::String(v) => v.parse::<f64>().unwrap_or(0.0),
        _ => 0.0,
    }
}

pub(crate) fn convert_to_long(value: &KqlValue) -> i64 {
    match value {
        KqlValue::Long(v) => *v,
        KqlValue::Real(v) => *v as i64,
        KqlValue::String(v) => v.parse::<i64>().unwrap_or(0),
        _ => 0,
    }
}

pub(crate) fn convert_to_bool(value: &KqlValue) -> Option<bool> {
    match value {
        KqlValue::Null => None,
        KqlValue::Bool(v) => Some(*v),
        KqlValue::Long(v) => Some(*v != 0),
        KqlValue::Real(v) => Some(*v != 0.0),
        KqlValue::String(v) => v.parse::<bool>().ok().or(Some(!v.is_empty())),
        _ => Some(true),
    }
}

pub(crate) fn convert_to_datetime(value: &KqlValue) -> Option<DateTime<Utc>> {
    match value {
        KqlValue::DateTime(v) => Some(*v),
        KqlValue::String(v) => DateTime::parse_from_rfc3339(v)
            .map(|dt| dt.with_timezone(&Utc))
            .ok(),
        KqlValue::Long(v) if *v > 1_000_000_000_000 => DateTime::from_timestamp_millis(*v),
        KqlValue::Long(v) => DateTime::from_timestamp(*v, 0),
        _ => None,
    }
}

pub(crate) fn is_truthy_true(value: &KqlValue) -> bool {
    matches!(value, KqlValue::Bool(true))
}

pub(crate) fn row_get(row: &Map<String, Value>, name: &str) -> KqlValue {
    row.get(name).map(KqlValue::from).unwrap_or(KqlValue::Null)
}

fn is_numeric(value: &KqlValue) -> bool {
    matches!(value, KqlValue::Long(_) | KqlValue::Real(_))
}

fn format_real(value: f64) -> String {
    if value.fract() == 0.0 {
        format!("{value:.0}")
    } else {
        value.to_string()
    }
}

fn format_duration(value: Duration) -> String {
    let millis = value.num_milliseconds();
    if millis % 86_400_000 == 0 {
        format!("{}d", millis / 86_400_000)
    } else if millis % 3_600_000 == 0 {
        format!("{}h", millis / 3_600_000)
    } else if millis % 60_000 == 0 {
        format!("{}m", millis / 60_000)
    } else if millis % 1_000 == 0 {
        format!("{}s", millis / 1_000)
    } else {
        format!("{millis}ms")
    }
}
