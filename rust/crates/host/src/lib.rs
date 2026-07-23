//! Application host. Ports the .NET `Host` project: assembles storage/auth/query
//! services, builds the middleware pipeline, and serves the Explorer SPA.
//!
//! The Explorer React app is NOT ported — its committed build (`wwwroot/explorer/`)
//! is embedded into the host binary via [`rust_embed`] and served under
//! `/explorer` with an SPA fallback (parity with the .NET
//! `ManifestEmbeddedFileProvider`). An explicit `--explorer-dir` overrides this
//! to serve from a physical directory.

use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;

use axum::{http::HeaderMap, routing::get, Json, Router};
use cosmos_core::models::VectorIndexOptions;
use cosmos_core::traits::{
    ActivityStore, AuthProvider, DocumentStore, EmulatorInfoService, QueryTelemetryStore,
    VectorIndexProvider,
};
use cosmos_core::{ConsistencyLevel, StorageType};
use cosmos_nosql::AppState;
use cosmos_storage::{
    FlatVectorIndexProvider, InMemoryDocumentStore, SqliteDocumentStore, SurrealDbDocumentStore,
    VectorIndexingDocumentStore,
};
use serde_json::json;

mod admin;
mod explorer;
mod info;
mod maintenance;
mod monitoring;
mod throughput;
mod tracking;

/// Default NoSQL REST API port, matching the .NET emulator.
pub const DEFAULT_PORT: u16 = 8081;

/// Options controlling how the host is built.
pub struct HostOptions {
    pub port: u16,
    /// Optional MongoDB wire-protocol port. When `Some`, the host starts the
    /// MongoDB listener in the background alongside the NoSQL REST API.
    pub mongo_port: Option<u16>,
    /// Enables HTTPS on the NoSQL endpoint with a local self-signed dev cert.
    pub enable_ssl: bool,
    /// Storage backend to use. Defaults to [`StorageType::Sqlite`] (the real
    /// default), matching the .NET emulator.
    pub storage: StorageType,
    /// Directory for persistent data (used by the Sqlite/SurrealDb backends).
    pub data_dir: Option<PathBuf>,
    /// Optional directory containing the built Explorer SPA to serve at `/explorer`.
    pub explorer_dir: Option<std::path::PathBuf>,
    /// Master key for HMAC authentication. When `Some`, the NoSQL API enforces
    /// signed `Authorization` headers; when `None`, authentication is skipped
    /// (dev/bring-up mode).
    pub master_key: Option<String>,
    /// Enables EntraID bearer token auth in addition to master-key auth.
    pub enable_entra: bool,
    /// Enforces database/container RU-per-second caps when enabled.
    pub enable_throughput_enforcement: bool,
    /// Starts background TTL cleanup and bounded-data maintenance tasks.
    pub enable_maintenance: bool,
    /// Adds Cosmos diagnostics headers and records recent request/RU activity.
    pub enable_request_tracking: bool,
    /// Default consistency level for session-token issuance/validation.
    pub consistency: ConsistencyLevel,
}

impl Default for HostOptions {
    fn default() -> Self {
        Self {
            port: DEFAULT_PORT,
            mongo_port: None,
            enable_ssl: false,
            storage: StorageType::Sqlite,
            data_dir: None,
            explorer_dir: None,
            master_key: None,
            enable_entra: false,
            enable_throughput_enforcement: false,
            enable_maintenance: false,
            enable_request_tracking: false,
            consistency: ConsistencyLevel::Session,
        }
    }
}

/// Constructs the storage backend selected by [`HostOptions`], wrapped in the
/// vector-indexing decorator (mirroring the .NET DI composition:
/// concrete store → vector index provider → `VectorIndexingDocumentStore`).
pub async fn build_store(opts: &HostOptions) -> Result<Arc<dyn DocumentStore>, anyhow::Error> {
    let inner: Arc<dyn DocumentStore> = match opts.storage {
        StorageType::InMemory => Arc::new(InMemoryDocumentStore::new()),
        StorageType::Sqlite => {
            let dir = opts
                .data_dir
                .clone()
                .unwrap_or_else(|| PathBuf::from("./cosmos-data"));
            Arc::new(SqliteDocumentStore::open(dir)?)
        }
        StorageType::SurrealDb => {
            let dir = opts
                .data_dir
                .clone()
                .unwrap_or_else(|| PathBuf::from("./cosmos-data"));
            Arc::new(SurrealDbDocumentStore::open(dir).await?)
        }
    };
    let index = Arc::new(FlatVectorIndexProvider::new(
        inner.clone(),
        VectorIndexOptions::default(),
    ));
    let store: Arc<dyn DocumentStore> = Arc::new(VectorIndexingDocumentStore::new(
        inner,
        index as Arc<dyn VectorIndexProvider>,
    ));
    Ok(store)
}

/// Builds the top-level Axum router (health + NoSQL API + optional Explorer)
/// around an already-constructed store.
pub fn build_router(opts: &HostOptions, store: Arc<dyn DocumentStore>) -> Router {
    let programmability_record_store =
        Arc::new(cosmos_storage::InMemoryProgrammabilityRecordStore::new());
    let programmability_query_engine: Arc<dyn cosmos_core::traits::QueryEngine> =
        Arc::new(cosmos_query::SqlQueryEngine::new(store.clone()));
    let programmability = Arc::new(cosmos_triggers::JsProgrammabilityEngine::with_record_store(
        store.clone(),
        programmability_query_engine,
        programmability_record_store,
    ));
    let udf_resolver: Arc<dyn cosmos_query::UdfResolver> = programmability.clone();
    let query_engine = Arc::new(cosmos_query::SqlQueryEngine::with_udf_resolver(
        store.clone(),
        udf_resolver,
    ));
    let activity_store: Arc<dyn ActivityStore> =
        Arc::new(cosmos_storage::InMemoryActivityStore::new());
    let telemetry_store: Arc<dyn QueryTelemetryStore> =
        Arc::new(cosmos_storage::InMemoryQueryTelemetryStore::new());
    let admin_settings = admin::AdminSettingsStore::new(opts.data_dir.clone(), opts.enable_entra);
    let info_service = Arc::new(info::HostEmulatorInfoService::new(
        opts,
        store.clone(),
        admin_settings.clone(),
    ));
    let admin_state = admin::AdminConfigState::new(opts);
    let mut state = AppState::new(store)
        .with_query_engine(query_engine)
        .with_programmability_engine(programmability)
        .with_emulator_info_service(info_service.clone() as Arc<dyn EmulatorInfoService>)
        .with_consistency(opts.consistency);

    let master_key = opts.master_key.as_ref().filter(|k| !k.is_empty()).cloned();
    if opts.enable_entra {
        let mut providers: Vec<Box<dyn AuthProvider>> = Vec::new();
        if let Some(key) = master_key {
            providers.push(Box::new(cosmos_auth::MasterKeyAuthProvider::new(key)));
        }
        providers.push(Box::new(cosmos_auth::EntraIdAuthProvider::new(
            true, None, None,
        )));
        state = state.with_auth(Arc::new(cosmos_auth::CompositeAuthProvider::new(providers)));
    } else if let Some(key) = master_key {
        state = state.with_auth(Arc::new(cosmos_auth::MasterKeyAuthProvider::new(key)));
    }

    let store_for_middleware = state.store.clone();
    let account_service = info_service.clone();
    let mut app = Router::new()
        .route(
            "/",
            get(move |headers: HeaderMap| {
                let account_service = account_service.clone();
                async move { Json(account_service.account_metadata(&headers)) }
            }),
        )
        .route("/health", get(health))
        .merge(admin::router(admin_state))
        .merge(monitoring::router(
            activity_store.clone(),
            telemetry_store.clone(),
        ))
        .merge(cosmos_nosql::router(state));

    if let Some(dir) = &opts.explorer_dir {
        // Explicit override: serve the SPA from a physical directory.
        app = app.nest_service("/explorer", tower_http::services::ServeDir::new(dir));
    } else if explorer::is_available() {
        // Default: serve the Explorer SPA embedded in the binary (parity with
        // the .NET host's ManifestEmbeddedFileProvider).
        app = app.merge(explorer::router());
    }

    if opts.enable_throughput_enforcement {
        let throughput_state = throughput::ThroughputState::new(store_for_middleware);
        app = app.layer(axum::middleware::from_fn_with_state(
            throughput_state,
            throughput::enforce,
        ));
    }

    if opts.enable_request_tracking {
        app = app.layer(axum::middleware::from_fn_with_state(
            tracking::TrackingState::new(Some(activity_store)),
            tracking::track,
        ));
    }

    app
}

async fn health() -> Json<serde_json::Value> {
    Json(json!({ "status": "ok" }))
}

/// Boots the host and serves until shutdown.
pub async fn run(opts: HostOptions) -> Result<(), anyhow::Error> {
    let store = build_store(&opts).await?;
    if opts.enable_maintenance {
        maintenance::spawn(store.clone(), opts.consistency);
    }
    let app = build_router(&opts, store);
    let addr = SocketAddr::from(([0, 0, 0, 0], opts.port));
    if let Some(mongo_port) = opts.mongo_port {
        tokio::spawn(async move {
            if let Err(error) = cosmos_mongodb::serve(mongo_port).await {
                tracing::error!(%error, mongo_port, "MongoDB listener stopped");
            }
        });
    }
    tracing::info!(
        "NoSQL Endpoint: {}://localhost:{} (storage: {:?})",
        if opts.enable_ssl { "https" } else { "http" },
        opts.port,
        opts.storage
    );
    if opts.explorer_dir.is_none() && explorer::is_available() {
        tracing::info!(
            "Explorer: {}://localhost:{}/explorer (embedded)",
            if opts.enable_ssl { "https" } else { "http" },
            opts.port
        );
    }
    if opts.enable_ssl {
        let config = tls_config(&opts).await?;
        axum_server::bind_rustls(addr, config)
            .serve(app.into_make_service())
            .await?;
    } else {
        let listener = tokio::net::TcpListener::bind(addr).await?;
        axum::serve(listener, app).await?;
    }
    Ok(())
}

async fn tls_config(
    opts: &HostOptions,
) -> Result<axum_server::tls_rustls::RustlsConfig, anyhow::Error> {
    let data_dir = opts
        .data_dir
        .clone()
        .unwrap_or_else(|| PathBuf::from("./cosmos-data"));
    let cert_dir = data_dir.join("certs");
    let cert_path = cert_dir.join("localhost.pem");
    let key_path = cert_dir.join("localhost-key.pem");
    if !cert_path.exists() || !key_path.exists() {
        std::fs::create_dir_all(&cert_dir)?;
        let certified = rcgen::generate_simple_self_signed(vec!["localhost".to_string()])?;
        std::fs::write(&cert_path, certified.cert.pem())?;
        std::fs::write(&key_path, certified.key_pair.serialize_pem())?;
    }
    Ok(axum_server::tls_rustls::RustlsConfig::from_pem_file(cert_path, key_path).await?)
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;
    use axum::http::{Request, StatusCode};
    use tower::ServiceExt;

    #[tokio::test]
    async fn health_endpoint_returns_ok() {
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(&HostOptions::default(), store);
        let resp = app
            .oneshot(
                Request::builder()
                    .uri("/health")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn dbs_endpoint_is_wired() {
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(&HostOptions::default(), store);
        let resp = app
            .oneshot(Request::builder().uri("/dbs").body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn account_root_returns_database_account_metadata() {
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(&HostOptions::default(), store);
        let resp = app
            .oneshot(
                Request::builder()
                    .uri("/")
                    .header("host", "localhost:8081")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);

        let bytes = axum::body::to_bytes(resp.into_body(), usize::MAX)
            .await
            .unwrap();
        let body: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(body["id"], "localhost");
        assert_eq!(body["_self"], "");
        assert_eq!(
            body["writableLocations"][0]["databaseAccountEndpoint"],
            "http://localhost:8081/"
        );
        assert_eq!(
            body["readableLocations"][0]["databaseAccountEndpoint"],
            "http://localhost:8081/"
        );
        assert_eq!(
            body["userConsistencyPolicy"]["defaultConsistencyLevel"],
            "Session"
        );
        assert_eq!(body["enableMultipleWriteLocations"], false);
        assert!(body["queryEngineConfiguration"].as_str().is_some());
    }

    #[tokio::test]
    async fn emulator_config_endpoints_round_trip() {
        let data_dir = test_data_dir("admin-config");
        let _ = std::fs::remove_dir_all(&data_dir);
        std::fs::create_dir_all(&data_dir).unwrap();

        let opts = HostOptions {
            data_dir: Some(data_dir.clone()),
            storage: StorageType::Sqlite,
            ..HostOptions::default()
        };
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(&opts, store);

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .uri("/api/emulator/config")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
        let body = json_body(resp).await;
        assert_eq!(body["storage"], "Sqlite");
        assert_eq!(body["dataDirectory"], data_dir.to_string_lossy().as_ref());
        assert_eq!(body["restartRequired"], false);

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("PUT")
                    .uri("/api/emulator/config")
                    .header("content-type", "application/json")
                    .body(Body::from(
                        r#"{"storage":"InMemory","dataDirectory":"./other-data"}"#,
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
        let body = json_body(resp).await;
        assert_eq!(body["storage"], "InMemory");
        assert_eq!(body["dataDirectory"], "./other-data");
        assert_eq!(body["restartRequired"], true);
        assert!(data_dir.join("emulator-config.json").exists());

        let resp = app
            .oneshot(
                Request::builder()
                    .method("PUT")
                    .uri("/api/emulator/settings")
                    .header("content-type", "application/json")
                    .body(Body::from(
                        r#"{"enableEntraId":true,"tenantId":" tenant ","clientId":" client "}"#,
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
        let body = json_body(resp).await;
        assert_eq!(body["configuration"]["enableEntraId"], true);
        assert_eq!(body["configuration"]["tenantId"], "tenant");
        assert_eq!(body["configuration"]["clientId"], "client");
        assert!(data_dir.join("admin-settings.json").exists());

        let _ = std::fs::remove_dir_all(&data_dir);
    }

    #[tokio::test]
    async fn monitoring_endpoints_expose_activity_telemetry_and_kql() {
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(
            &HostOptions {
                enable_request_tracking: true,
                ..HostOptions::default()
            },
            store,
        );

        let resp = app
            .clone()
            .oneshot(Request::builder().uri("/dbs").body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .uri("/api/emulator/activity")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
        let body = json_body(resp).await;
        assert_eq!(body.as_array().unwrap().len(), 1);
        assert_eq!(body[0]["method"], "GET");
        assert_eq!(body[0]["path"], "/dbs");

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .uri("/api/emulator/telemetry?max=10")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
        assert!(json_body(resp).await.as_array().unwrap().is_empty());

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/api/emulator/kql")
                    .header("content-type", "application/json")
                    .body(Body::from(
                        r#"{"query":"activity | where path == '/dbs' | take 1 | project method, path"}"#,
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
        let body = json_body(resp).await;
        assert_eq!(body["columns"][0]["name"], "method");
        assert_eq!(body["columns"][1]["name"], "path");
        assert_eq!(body["rows"][0][0], "GET");
        assert_eq!(body["rows"][0][1], "/dbs");

        let resp = app
            .oneshot(
                Request::builder()
                    .method("DELETE")
                    .uri("/api/emulator/telemetry")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::NO_CONTENT);
    }

    #[tokio::test]
    async fn sql_udf_executes_through_host_programmability_resolver() {
        let store = Arc::new(InMemoryDocumentStore::new());
        let app = build_router(&HostOptions::default(), store);

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/dbs")
                    .header("content-type", "application/json")
                    .body(Body::from(r#"{"id":"udfdb"}"#))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::CREATED);

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/dbs/udfdb/colls")
                    .header("content-type", "application/json")
                    .body(Body::from(
                        r#"{"id":"udfcoll","partitionKey":{"paths":["/id"],"kind":"Hash","version":2}}"#,
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::CREATED);

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/dbs/udfdb/colls/udfcoll/udfs")
                    .header("content-type", "application/json")
                    .body(Body::from(
                        r#"{"id":"double","body":"function(v) { return v * 2; }"}"#,
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::CREATED);

        let resp = app
            .clone()
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/dbs/udfdb/colls/udfcoll/docs")
                    .header("content-type", "application/json")
                    .body(Body::from(r#"{"id":"doc1","x":21}"#))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::CREATED);

        let resp = app
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/dbs/udfdb/colls/udfcoll/docs")
                    .header("content-type", "application/query+json")
                    .header("x-ms-documentdb-isquery", "true")
                    .header("x-ms-documentdb-query-enablecrosspartition", "true")
                    .body(Body::from(
                        r#"{"query":"SELECT udf.double(c.x) AS doubled FROM c","parameters":[]}"#,
                    ))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(resp.status(), StatusCode::OK);
        let body = json_body(resp).await;
        assert_eq!(body["Documents"][0]["doubled"], 42);
    }

    async fn json_body(resp: axum::response::Response) -> serde_json::Value {
        let bytes = axum::body::to_bytes(resp.into_body(), usize::MAX)
            .await
            .unwrap();
        serde_json::from_slice(&bytes).unwrap()
    }

    fn test_data_dir(name: &str) -> PathBuf {
        std::env::current_dir()
            .unwrap()
            .join("target")
            .join(format!(
                "cosmos-host-{name}-{}",
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ))
    }
}
