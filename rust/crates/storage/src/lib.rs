//! Storage backends for the Cosmos DB light emulator.
//!
//! Ports the .NET `Storage` project. Backends implement [`cosmos_core::traits::DocumentStore`].
//! Port order (see roadmap): **InMemory first**, then Sqlite (the real default),
//! then SurrealDb. Change-feed (LSN) and vector (HNSW) providers follow.

pub mod changefeed;
pub mod common;
pub mod inmemory;
pub mod sqlite;

pub use changefeed::{InMemoryChangeFeedProvider, InMemoryChangeLog, SqliteChangeFeedProvider};
pub use inmemory::InMemoryDocumentStore;
pub use sqlite::SqliteDocumentStore;
