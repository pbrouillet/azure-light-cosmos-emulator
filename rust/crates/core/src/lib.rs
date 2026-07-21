//! Core domain models and traits for the Cosmos DB light emulator.
//!
//! Ports the .NET `Azure.Cosmos.LightEmulator.Core` project: domain models
//! (databases, containers, documents, headers, consistency, vector/spatial) and
//! the core traits (`DocumentStore`, `QueryEngine`, `AuthProvider`,
//! `ChangeFeedProvider`, `ConsistencyManager`, `ProgrammabilityEngine`).
//!
//! This is a pure library with no external service dependencies, mirroring the
//! .NET Core project which sits at the bottom of the dependency graph.

pub mod models;
pub mod traits;

/// Cosmos DB consistency levels. All five are accepted; only Session tokens are
/// actually enforced by the single-node emulator (parity with the .NET impl).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, serde::Serialize, serde::Deserialize)]
pub enum ConsistencyLevel {
    Strong,
    BoundedStaleness,
    #[default]
    Session,
    ConsistentPrefix,
    Eventual,
}
