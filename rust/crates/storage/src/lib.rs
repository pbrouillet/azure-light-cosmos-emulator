//! Storage backends for the Cosmos DB light emulator.
//!
//! Ports the .NET `Storage` project. Backends implement [`cosmos_core::traits::DocumentStore`].
//! Port order (see roadmap): **InMemory first**, then Sqlite (the real default),
//! then SurrealDb. Change-feed (LSN) and vector (HNSW) providers follow.

pub mod changefeed;
pub mod common;
pub mod inmemory;
pub mod programmability;
pub mod sqlite;
pub mod surreal;
pub mod vector;

pub use changefeed::{InMemoryChangeFeedProvider, InMemoryChangeLog, SqliteChangeFeedProvider};
pub use inmemory::{
    InMemoryActivityStore, InMemoryDocumentStore, InMemoryProgrammabilityRecordStore,
    InMemoryQueryTelemetryStore,
};
pub use programmability::{
    make_record_key, ProgrammabilityRecord, ProgrammabilityRecordStore, ProgrammabilityTable,
};
pub use sqlite::{
    SqliteActivityStore, SqliteDocumentStore, SqliteProgrammabilityRecordStore,
    SqliteQueryTelemetryStore,
};
pub use surreal::{
    SurrealDbActivityStore, SurrealDbChangeFeedProvider, SurrealDbDocumentStore,
    SurrealDbProgrammabilityRecordStore, SurrealDbQueryTelemetryStore,
};
pub use vector::{FlatVectorIndexProvider, HnswVectorIndexProvider, VectorIndexingDocumentStore};
