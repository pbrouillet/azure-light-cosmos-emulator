//! Partition key value and definition. Ports `PartitionKeyValue.cs` and
//! `PartitionKeyDefinition.cs`.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// The resolved partition key value(s) for a document.
///
/// Components are stored as JSON values. Equality and hashing use the canonical
/// header string so a `PartitionKeyValue` can serve as a map key.
#[derive(Debug, Clone, Default)]
pub struct PartitionKeyValue {
    pub components: Vec<Value>,
}

impl PartitionKeyValue {
    /// Creates a single-component partition key.
    pub fn single(value: Value) -> Self {
        Self {
            components: vec![value],
        }
    }

    /// Creates a multi-component (hierarchical) partition key.
    pub fn multi(values: Vec<Value>) -> Self {
        Self { components: values }
    }

    /// An undefined partition key (no components).
    pub fn undefined() -> Self {
        Self {
            components: Vec::new(),
        }
    }

    /// Serializes to the Cosmos header format: `["value"]` or `["v1","v2"]`.
    pub fn to_header_string(&self) -> String {
        if self.components.is_empty() {
            return "[]".to_string();
        }
        let parts: Vec<String> = self
            .components
            .iter()
            .map(|c| match c {
                Value::Null => "null".to_string(),
                Value::String(s) => serde_json::to_string(s).unwrap_or_else(|_| "null".into()),
                Value::Bool(b) => b.to_string(),
                other => other.to_string(),
            })
            .collect();
        format!("[{}]", parts.join(","))
    }
}

impl PartialEq for PartitionKeyValue {
    fn eq(&self, other: &Self) -> bool {
        self.to_header_string() == other.to_header_string()
    }
}

impl Eq for PartitionKeyValue {}

impl std::hash::Hash for PartitionKeyValue {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.to_header_string().hash(state);
    }
}

/// The kind of partition key.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum PartitionKeyKind {
    Hash,
    Range,
    MultiHash,
}

/// Defines the partition key path(s) for a container.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PartitionKeyDefinition {
    /// Partition key paths, e.g. `["/tenantId"]`.
    pub paths: Vec<String>,
    #[serde(default = "default_pk_kind")]
    pub kind: PartitionKeyKind,
    #[serde(default = "default_pk_version")]
    pub version: i32,
}

fn default_pk_kind() -> PartitionKeyKind {
    PartitionKeyKind::Hash
}

fn default_pk_version() -> i32 {
    2
}

impl PartitionKeyDefinition {
    pub fn new(paths: Vec<String>) -> Self {
        Self {
            paths,
            kind: default_pk_kind(),
            version: default_pk_version(),
        }
    }
}
