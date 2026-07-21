//! Programmability resources: stored procedures, triggers, UDFs.
//! Ports `Programmability.cs`.

use crate::ids::{etag, resource_id};

fn now_ts() -> i64 {
    chrono::Utc::now().timestamp()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TriggerType {
    Pre,
    Post,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TriggerOperation {
    All,
    Create,
    Replace,
    Delete,
}

#[derive(Debug, Clone)]
pub struct StoredProcedure {
    pub id: String,
    pub rid: String,
    pub self_link: String,
    pub etag: String,
    pub timestamp: i64,
    pub database_id: String,
    pub container_id: String,
    /// JavaScript function body.
    pub body: String,
}

#[derive(Debug, Clone)]
pub struct Trigger {
    pub id: String,
    pub rid: String,
    pub self_link: String,
    pub etag: String,
    pub timestamp: i64,
    pub database_id: String,
    pub container_id: String,
    pub body: String,
    pub trigger_type: TriggerType,
    pub trigger_operation: TriggerOperation,
}

#[derive(Debug, Clone)]
pub struct UserDefinedFunction {
    pub id: String,
    pub rid: String,
    pub self_link: String,
    pub etag: String,
    pub timestamp: i64,
    pub database_id: String,
    pub container_id: String,
    pub body: String,
}

impl StoredProcedure {
    pub fn new(
        database_id: impl Into<String>,
        container_id: impl Into<String>,
        id: impl Into<String>,
        body: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            rid: resource_id(),
            self_link: String::new(),
            etag: etag(),
            timestamp: now_ts(),
            database_id: database_id.into(),
            container_id: container_id.into(),
            body: body.into(),
        }
    }
}
