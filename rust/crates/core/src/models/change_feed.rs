//! Change feed event model. Ports `ChangeFeedItem.cs`.

use chrono::{DateTime, Utc};

use crate::models::resources::CosmosDocument;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChangeType {
    Create,
    Replace,
    Delete,
}

/// A change feed event for a document.
#[derive(Debug, Clone)]
pub struct ChangeFeedItem {
    pub document: CosmosDocument,
    pub lsn: i64,
    pub change_type: ChangeType,
    /// Previous image of the document (full-fidelity mode).
    pub previous_image: Option<CosmosDocument>,
    pub timestamp: DateTime<Utc>,
}

impl ChangeFeedItem {
    pub fn new(document: CosmosDocument, lsn: i64, change_type: ChangeType) -> Self {
        Self {
            document,
            lsn,
            change_type,
            previous_image: None,
            timestamp: Utc::now(),
        }
    }
}
