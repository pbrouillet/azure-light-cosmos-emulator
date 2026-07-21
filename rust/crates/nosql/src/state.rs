//! Shared application state and lifecycle info for the NoSQL API.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use cosmos_core::consistency::ConsistencyManager;
use cosmos_core::traits::{AuthProvider, ChangeFeedProvider, DocumentStore, QueryEngine};

/// Emulator lifecycle timestamps reused across responses. Ports
/// `EmulatorRuntimeState`.
#[derive(Clone)]
pub struct RuntimeState {
    pub started_at: DateTime<Utc>,
}

impl Default for RuntimeState {
    fn default() -> Self {
        Self {
            started_at: Utc::now(),
        }
    }
}

/// Shared application state passed to handlers.
///
/// Centralizing service construction here avoids the .NET "three DI surfaces"
/// synchronization trap (`Program.cs` / `HostApplication.cs` / test fixture).
#[derive(Clone)]
pub struct AppState {
    /// Primary document store.
    pub store: Arc<dyn DocumentStore>,
    /// Change-feed provider (shares the store's backing data). Optional so the
    /// host can omit it during early bring-up.
    pub change_feed: Option<Arc<dyn ChangeFeedProvider>>,
    /// SQL query engine. When absent, the query endpoint falls back to a naive
    /// "return all documents" behaviour (see `documents::execute_query`), which
    /// is replaced by the real engine in the `query-crate` phase.
    pub query_engine: Option<Arc<dyn QueryEngine>>,
    /// Consistency manager (session-token issuance/validation).
    pub consistency: Arc<ConsistencyManager>,
    /// Authentication provider. When present, the auth layer enforces it; when
    /// absent, authentication is skipped (dev/bring-up mode).
    pub auth: Option<Arc<dyn AuthProvider>>,
    /// Lifecycle timestamps.
    pub runtime: RuntimeState,
}

impl AppState {
    /// Builds state from a store alone, with default consistency and no auth,
    /// change feed, or query engine (used by the early host slice and tests).
    pub fn new(store: Arc<dyn DocumentStore>) -> Self {
        Self {
            store,
            change_feed: None,
            query_engine: None,
            consistency: Arc::new(ConsistencyManager::default()),
            auth: None,
            runtime: RuntimeState::default(),
        }
    }

    pub fn with_change_feed(mut self, change_feed: Arc<dyn ChangeFeedProvider>) -> Self {
        self.change_feed = Some(change_feed);
        self
    }

    pub fn with_query_engine(mut self, query_engine: Arc<dyn QueryEngine>) -> Self {
        self.query_engine = Some(query_engine);
        self
    }

    pub fn with_auth(mut self, auth: Arc<dyn AuthProvider>) -> Self {
        self.auth = Some(auth);
        self
    }
}
