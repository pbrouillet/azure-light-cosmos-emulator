//! Shared application state and lifecycle info for the NoSQL API.

use std::sync::Arc;
use std::sync::Mutex;

use chrono::{DateTime, Utc};
use cosmos_core::consistency::ConsistencyManager;
use cosmos_core::traits::{
    AuthProvider, ChangeFeedProvider, DocumentStore, EmulatorInfoService, QueryEngine,
};
use cosmos_query::SqlQueryEngine;
use cosmos_triggers::JsProgrammabilityEngine;

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

/// Endpoint settings reported by the `/addresses` routing-map endpoint.
#[derive(Clone)]
pub struct AddressEndpoint {
    pub port: u16,
    pub enable_ssl: bool,
}

impl Default for AddressEndpoint {
    fn default() -> Self {
        Self {
            port: 8081,
            enable_ssl: false,
        }
    }
}

/// Mutable admin settings surfaced by `/api/emulator/info` and updated by
/// `/api/emulator/settings` when no host-provided info service is installed.
#[derive(Clone, Debug, Default)]
pub struct EmulatorSettings {
    pub enable_entra_id: bool,
    pub tenant_id: Option<String>,
    pub client_id: Option<String>,
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
    /// Stored procedure/trigger/UDF engine.
    pub programmability: Arc<JsProgrammabilityEngine>,
    /// Consistency manager (session-token issuance/validation).
    pub consistency: Arc<ConsistencyManager>,
    /// Authentication provider. When present, the auth layer enforces it; when
    /// absent, authentication is skipped (dev/bring-up mode).
    pub auth: Option<Arc<dyn AuthProvider>>,
    /// Lifecycle timestamps.
    pub runtime: RuntimeState,
    /// Endpoint values surfaced by `GET /addresses`.
    pub address_endpoint: AddressEndpoint,
    /// Host-provided info/settings service, when available.
    pub emulator_info: Option<Arc<dyn EmulatorInfoService>>,
    /// In-memory settings used by the default NoSQL-local info service.
    pub emulator_settings: Arc<Mutex<EmulatorSettings>>,
}

impl AppState {
    /// Builds state from a store alone, with default consistency and no auth,
    /// change feed, or query engine (used by the early host slice and tests).
    pub fn new(store: Arc<dyn DocumentStore>) -> Self {
        let programmability_query_engine: Arc<dyn QueryEngine> =
            Arc::new(SqlQueryEngine::new(store.clone()));
        let programmability = Arc::new(JsProgrammabilityEngine::new(
            store.clone(),
            programmability_query_engine,
        ));
        Self {
            store,
            change_feed: None,
            query_engine: None,
            programmability,
            consistency: Arc::new(ConsistencyManager::default()),
            auth: None,
            runtime: RuntimeState::default(),
            address_endpoint: AddressEndpoint::default(),
            emulator_info: None,
            emulator_settings: Arc::new(Mutex::new(EmulatorSettings::default())),
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

    pub fn with_programmability_engine(
        mut self,
        programmability: Arc<JsProgrammabilityEngine>,
    ) -> Self {
        self.programmability = programmability;
        self
    }

    pub fn with_auth(mut self, auth: Arc<dyn AuthProvider>) -> Self {
        self.auth = Some(auth);
        self
    }

    pub fn with_emulator_info_service(mut self, service: Arc<dyn EmulatorInfoService>) -> Self {
        self.emulator_info = Some(service);
        self
    }

    /// Overrides the default consistency level used for session-token issuance
    /// and validation.
    pub fn with_consistency(mut self, level: cosmos_core::ConsistencyLevel) -> Self {
        self.consistency = Arc::new(ConsistencyManager::new(level));
        self
    }

    /// Overrides the endpoint values returned by `GET /addresses`.
    pub fn with_address_endpoint(mut self, port: u16, enable_ssl: bool) -> Self {
        self.address_endpoint = AddressEndpoint { port, enable_ssl };
        self
    }
}
