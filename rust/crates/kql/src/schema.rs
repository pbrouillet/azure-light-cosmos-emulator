use std::collections::HashMap;
use std::sync::{Arc, RwLock};

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::result::Row;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct KqlColumnSchema {
    pub name: String,
    pub kql_type: String,
}

impl KqlColumnSchema {
    pub fn new(name: impl Into<String>, kql_type: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            kql_type: kql_type.into(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct KqlTableSchema {
    pub table_name: String,
    pub columns: Vec<KqlColumnSchema>,
}

impl KqlTableSchema {
    pub fn new(table_name: impl Into<String>, columns: Vec<KqlColumnSchema>) -> Self {
        Self {
            table_name: table_name.into(),
            columns,
        }
    }
}

#[derive(Debug, Clone, Default)]
pub struct KqlSchemaRegistry {
    tables: Arc<RwLock<HashMap<String, KqlTableSchema>>>,
}

impl KqlSchemaRegistry {
    pub fn register_table(&self, schema: KqlTableSchema) {
        self.tables
            .write()
            .expect("schema registry lock poisoned")
            .insert(schema.table_name.to_ascii_lowercase(), schema);
    }

    pub fn get_table(&self, name: &str) -> Option<KqlTableSchema> {
        self.tables
            .read()
            .expect("schema registry lock poisoned")
            .get(&name.to_ascii_lowercase())
            .cloned()
    }

    pub fn get_all_tables(&self) -> Vec<KqlTableSchema> {
        self.tables
            .read()
            .expect("schema registry lock poisoned")
            .values()
            .cloned()
            .collect()
    }
}

pub(crate) fn infer_schema(rows: &[Row]) -> KqlTableSchema {
    let columns = rows
        .first()
        .map(|row| {
            row.keys()
                .map(|key| KqlColumnSchema::new(key.clone(), infer_column_type(rows, key)))
                .collect()
        })
        .unwrap_or_default();
    KqlTableSchema::new("result", columns)
}

fn infer_column_type(rows: &[Row], column: &str) -> &'static str {
    let sample = rows
        .iter()
        .filter_map(|row| row.get(column))
        .find(|value| !value.is_null());

    match sample {
        Some(Value::Number(n)) if n.is_i64() || n.is_u64() => "long",
        Some(Value::Number(_)) => "real",
        Some(Value::Bool(_)) => "bool",
        Some(Value::Array(_) | Value::Object(_)) => "dynamic",
        Some(Value::String(s)) if chrono::DateTime::parse_from_rfc3339(s).is_ok() => "datetime",
        _ => "string",
    }
}
