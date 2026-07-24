//! Persistence primitives for stored procedures, triggers, and UDFs.
//!
//! This mirrors the .NET `IProgrammabilityRecordStore` seam: the JavaScript
//! engine owns validation/execution while storage backends persist low-level
//! records in `cosmos_sprocs`, `cosmos_triggers`, and `cosmos_udfs`.

use async_trait::async_trait;
use base64::Engine;
use cosmos_core::error::CosmosResult;
use cosmos_core::models::programmability::{StoredProcedure, Trigger, UserDefinedFunction};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProgrammabilityTable {
    StoredProcedures,
    Triggers,
    UserDefinedFunctions,
}

impl ProgrammabilityTable {
    pub fn name(self) -> &'static str {
        match self {
            Self::StoredProcedures => "cosmos_sprocs",
            Self::Triggers => "cosmos_triggers",
            Self::UserDefinedFunctions => "cosmos_udfs",
        }
    }
}

#[derive(Debug, Clone)]
pub enum ProgrammabilityRecord {
    StoredProcedure(StoredProcedure),
    Trigger(Trigger),
    UserDefinedFunction(UserDefinedFunction),
}

/// Low-level record store used by the JavaScript programmability engine.
#[async_trait]
pub trait ProgrammabilityRecordStore: Send + Sync {
    async fn select_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
    ) -> CosmosResult<Option<ProgrammabilityRecord>>;

    async fn select_table_records(
        &self,
        table: ProgrammabilityTable,
    ) -> CosmosResult<Vec<ProgrammabilityRecord>>;

    async fn create_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
        record: ProgrammabilityRecord,
    ) -> CosmosResult<()>;

    async fn upsert_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
        record: ProgrammabilityRecord,
    ) -> CosmosResult<()>;

    async fn delete_record(
        &self,
        table: ProgrammabilityTable,
        record_key: &str,
        resource_type: &str,
        resource_id: &str,
    ) -> CosmosResult<()>;
}

/// Builds the same SurrealDB record id as the .NET engine:
/// base64url(db):base64url(container):base64url(resource).
pub fn make_record_key(database_id: &str, container_id: &str, resource_id: &str) -> String {
    format!(
        "{}:{}:{}",
        encode_record_key(database_id),
        encode_record_key(container_id),
        encode_record_key(resource_id)
    )
}

fn encode_record_key(value: &str) -> String {
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(value.as_bytes())
}
