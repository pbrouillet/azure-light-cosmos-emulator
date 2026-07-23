//! Emulator runtime state model. Ports `EmulatorRuntimeState.cs`.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Captures emulator lifecycle timestamps that are reused across responses.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EmulatorRuntimeState {
    pub started_at_utc: DateTime<Utc>,
}

impl Default for EmulatorRuntimeState {
    fn default() -> Self {
        Self {
            started_at_utc: Utc::now(),
        }
    }
}

impl EmulatorRuntimeState {
    pub fn new() -> Self {
        Self::default()
    }
}
