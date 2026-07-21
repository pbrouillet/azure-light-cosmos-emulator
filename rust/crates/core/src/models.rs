//! Domain models. Ports `src/Core/Models/*` from the .NET project.
//!
//! Only a minimal set is scaffolded here; the full port fills these in during
//! the `core-crate` phase of the roadmap.

use serde::{Deserialize, Serialize};

/// A logical database (Cosmos `dbs`).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CosmosDatabase {
    pub id: String,
}

/// A container / collection (Cosmos `colls`).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CosmosContainer {
    pub id: String,
    /// Partition key path, e.g. `/pk`.
    #[serde(rename = "partitionKeyPath", default)]
    pub partition_key_path: Option<String>,
}

/// A stored JSON document with Cosmos system properties (`id`, `_rid`, etc.).
pub type CosmosDocument = serde_json::Value;
