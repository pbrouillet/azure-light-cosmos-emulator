//! Core domain models and traits for the Cosmos DB light emulator.
//!
//! Ports the .NET `Azure.Cosmos.LightEmulator.Core` project: domain models
//! (databases, containers, documents, headers, consistency, vector/spatial) and
//! the core traits (`DocumentStore`, `QueryEngine`, `ActivityStore`,
//! `QueryTelemetryStore`, `EmulatorInfoService`, `AuthProvider`,
//! `ChangeFeedProvider`, `ConsistencyManager`, `ProgrammabilityEngine`).
//!
//! This is a pure library with no external service dependencies, mirroring the
//! .NET Core project which sits at the bottom of the dependency graph.

use serde::{Deserialize, Serialize};

pub mod consistency;
pub mod error;
pub mod ids;
pub mod models;
pub mod traits;

pub use consistency::ConsistencyManager;
pub use error::{CosmosError, CosmosResult};

/// Cosmos DB consistency levels.
///
/// The variant order is significant: it defines the strength ordering used by
/// [`traits::ConsistencyManager::is_valid_consistency_level`]. A *larger*
/// discriminant is a *weaker* level (Strong is strongest, Eventual is weakest),
/// matching the .NET enum ordinals. All five are accepted; only Session tokens
/// are actually enforced by the single-node emulator.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default, Serialize, Deserialize)]
pub enum ConsistencyLevel {
    Strong,
    BoundedStaleness,
    #[default]
    Session,
    ConsistentPrefix,
    Eventual,
}

impl ConsistencyLevel {
    /// Parses a consistency level from its name (case-insensitive, ignoring
    /// spaces), mirroring the .NET emulator's tolerant `--consistency` parsing.
    /// Unknown values fall back to [`ConsistencyLevel::Session`].
    pub fn parse(value: &str) -> Self {
        match value
            .trim()
            .replace([' ', '-', '_'], "")
            .to_ascii_lowercase()
            .as_str()
        {
            "strong" => ConsistencyLevel::Strong,
            "boundedstaleness" => ConsistencyLevel::BoundedStaleness,
            "consistentprefix" => ConsistencyLevel::ConsistentPrefix,
            "eventual" => ConsistencyLevel::Eventual,
            _ => ConsistencyLevel::Session,
        }
    }
}

/// Storage backend types supported by the emulator. Ports `StorageType`.
///
/// Note: although `SurrealDb` is listed first (matching the .NET enum), the CLI
/// default is **Sqlite** — the storage registration parses null/unknown values
/// to `Sqlite`, not the first variant.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum StorageType {
    SurrealDb,
    Sqlite,
    InMemory,
}
