use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

use crate::schema::KqlTableSchema;

pub type Row = Map<String, Value>;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct KqlQueryResult {
    pub schema: KqlTableSchema,
    pub rows: Vec<Row>,
}

impl KqlQueryResult {
    pub fn new(schema: KqlTableSchema, rows: Vec<Row>) -> Self {
        Self { schema, rows }
    }
}
