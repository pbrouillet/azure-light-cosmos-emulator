use std::path::{Path, PathBuf};
use std::sync::Arc;

use async_trait::async_trait;
use axum::http::HeaderMap;
use cosmos_core::error::CosmosResult;
use cosmos_core::models::JsonObject;
use cosmos_core::traits::{DocumentStore, EmulatorInfoService};
use cosmos_core::ConsistencyLevel;
use serde_json::{json, Map, Value};

use crate::admin::AdminSettingsStore;
use crate::HostOptions;

const EMULATOR_NAME: &str = "Azure Cosmos DB Light Emulator";

/// Query-engine capability flags surfaced in the account metadata so SDKs know
/// which SQL features the service supports.
const QUERY_ENGINE_CONFIGURATION: &str = "{\"maxSqlQueryInputLength\":262144,\"maxJoinsPerSqlQuery\":5,\"maxLogicalAndPerSqlQuery\":500,\"maxLogicalOrPerSqlQuery\":500,\"maxUdfRefPerSqlQuery\":10,\"maxInExpressionItemsCount\":16000,\"queryMaxInMemorySortDocumentCount\":500,\"maxQueryRequestTimeoutFraction\":0.9,\"sqlAllowNonFiniteNumbers\":false,\"sqlAllowAggregateFunctions\":true,\"sqlAllowSubQuery\":true,\"sqlAllowScalarSubQuery\":true,\"allowNewKeywords\":true,\"sqlAllowLike\":true,\"sqlAllowGroupByClause\":true,\"maxSpatialQueryCells\":12,\"spatialMaxGeometryPointCount\":256,\"sqlDisableOptimizationFlags\":0,\"sqlAllowTop\":true,\"enableSpatialIndexing\":true}";

#[derive(Clone)]
pub struct HostEmulatorInfoService {
    config: Arc<EmulatorInfoConfig>,
    store: Arc<dyn DocumentStore>,
    admin_settings: AdminSettingsStore,
}

impl HostEmulatorInfoService {
    pub fn new(
        opts: &HostOptions,
        store: Arc<dyn DocumentStore>,
        admin_settings: AdminSettingsStore,
    ) -> Self {
        Self {
            config: Arc::new(EmulatorInfoConfig {
                port: opts.port,
                mongo_port: opts.mongo_port,
                storage: format!("{:?}", opts.storage),
                data_dir: opts.data_dir.clone(),
                master_key: opts.master_key.clone().unwrap_or_default(),
                consistency: opts.consistency,
                enable_ssl: opts.enable_ssl,
                enable_explorer: opts.explorer_dir.is_some(),
            }),
            store,
            admin_settings,
        }
    }

    pub fn account_metadata(&self, headers: &HeaderMap) -> Value {
        let host = headers
            .get("host")
            .and_then(|value| value.to_str().ok())
            .filter(|value| !value.trim().is_empty())
            .map(str::to_string)
            .unwrap_or_else(|| format!("localhost:{}", self.config.port));
        account_metadata_for_host(&host, self.config.enable_ssl, self.config.consistency)
    }

    fn no_sql_endpoint(&self) -> String {
        let scheme = if self.config.enable_ssl {
            "https"
        } else {
            "http"
        };
        format!("{scheme}://localhost:{}", self.config.port)
    }
}

#[async_trait]
impl EmulatorInfoService for HostEmulatorInfoService {
    async fn get_info(&self) -> CosmosResult<JsonObject> {
        let endpoint = self.no_sql_endpoint();
        let admin_settings = self.admin_settings.get_effective_settings();

        let mut endpoints = Map::new();
        endpoints.insert("noSql".into(), Value::String(endpoint.clone()));
        endpoints.insert(
            "mongoDb".into(),
            self.config
                .mongo_port
                .map(|port| Value::String(format!("mongodb://localhost:{port}")))
                .unwrap_or(Value::Null),
        );
        endpoints.insert(
            "explorer".into(),
            if self.config.enable_explorer {
                Value::String(format!("{endpoint}/explorer"))
            } else {
                Value::Null
            },
        );

        let mut configuration = Map::new();
        configuration.insert("port".into(), json!(self.config.port));
        configuration.insert("mongoPort".into(), json!(self.config.mongo_port));
        configuration.insert("storage".into(), json!(self.config.storage));
        configuration.insert("dataDirectory".into(), path_to_value(&self.config.data_dir));
        configuration.insert(
            "consistencyLevel".into(),
            json!(consistency_name(self.config.consistency)),
        );
        configuration.insert("enableSsl".into(), json!(self.config.enable_ssl));
        configuration.insert("enableExplorer".into(), json!(self.config.enable_explorer));
        configuration.insert(
            "enableEntraId".into(),
            json!(admin_settings.enable_entra_id),
        );
        configuration.insert("tenantId".into(), option_string(admin_settings.tenant_id));
        configuration.insert("clientId".into(), option_string(admin_settings.client_id));

        let mut result = Map::new();
        result.insert("name".into(), Value::String(EMULATOR_NAME.to_string()));
        result.insert(
            "version".into(),
            Value::String(env!("CARGO_PKG_VERSION").to_string()),
        );
        result.insert("endpoints".into(), Value::Object(endpoints));
        result.insert(
            "connectionString".into(),
            Value::String(format!(
                "AccountEndpoint={endpoint};AccountKey={};",
                self.config.master_key
            )),
        );
        result.insert(
            "masterKey".into(),
            Value::String(self.config.master_key.clone()),
        );
        result.insert("configuration".into(), Value::Object(configuration));
        Ok(result)
    }

    async fn get_stats(&self) -> CosmosResult<JsonObject> {
        let databases = self.store.list_databases().await?;
        let mut container_count = 0usize;
        for database in &databases.resources {
            container_count += self
                .store
                .list_containers(&database.id)
                .await?
                .resources
                .len();
        }

        let mut result = Map::new();
        result.insert("totalRequestUnits".into(), json!(0.0));
        result.insert("totalRequests".into(), json!(0));
        result.insert("databaseCount".into(), json!(databases.resources.len()));
        result.insert("containerCount".into(), json!(container_count));
        result.insert("documentCount".into(), json!(0));
        result.insert("dataDirectory".into(), path_to_value(&self.config.data_dir));
        result.insert(
            "dataSizeBytes".into(),
            json!(self
                .config
                .data_dir
                .as_deref()
                .map(directory_size)
                .unwrap_or(0)),
        );
        result.insert("uptimeSeconds".into(), json!(0));
        Ok(result)
    }

    async fn update_settings(
        &self,
        enable_entra_id: bool,
        tenant_id: Option<&str>,
        client_id: Option<&str>,
    ) -> CosmosResult<JsonObject> {
        self.admin_settings
            .update_settings(enable_entra_id, tenant_id, client_id)
            .map_err(|error| cosmos_core::CosmosError::internal_server_error(error.to_string()))?;
        self.get_info().await
    }
}

#[derive(Clone)]
struct EmulatorInfoConfig {
    port: u16,
    mongo_port: Option<u16>,
    storage: String,
    data_dir: Option<PathBuf>,
    master_key: String,
    consistency: ConsistencyLevel,
    enable_ssl: bool,
    enable_explorer: bool,
}

fn account_metadata_for_host(host: &str, enable_ssl: bool, consistency: ConsistencyLevel) -> Value {
    let scheme = if enable_ssl { "https" } else { "http" };
    let endpoint = format!("{scheme}://{host}/");
    let account_id = host.split(':').next().unwrap_or(host);
    let location = json!({
        "name": "Local",
        "databaseAccountEndpoint": endpoint,
    });

    json!({
        "_self": "",
        "id": account_id,
        "_rid": account_id,
        "media": "/media/",
        "addresses": "/addresses/",
        "_dbs": "/dbs/",
        "writableLocations": [location.clone()],
        "readableLocations": [location],
        "enableMultipleWriteLocations": false,
        "userReplicationPolicy": {
            "asyncReplication": false,
            "minReplicaSetSize": 1,
            "maxReplicasetSize": 4
        },
        "userConsistencyPolicy": {
            "defaultConsistencyLevel": consistency_name(consistency)
        },
        "systemReplicationPolicy": {
            "minReplicaSetSize": 1,
            "maxReplicasetSize": 4
        },
        "readPolicy": {
            "primaryReadCoefficient": 1,
            "secondaryReadCoefficient": 1
        },
        "queryEngineConfiguration": QUERY_ENGINE_CONFIGURATION
    })
}

fn consistency_name(consistency: ConsistencyLevel) -> &'static str {
    match consistency {
        ConsistencyLevel::Strong => "Strong",
        ConsistencyLevel::BoundedStaleness => "BoundedStaleness",
        ConsistencyLevel::Session => "Session",
        ConsistencyLevel::ConsistentPrefix => "ConsistentPrefix",
        ConsistencyLevel::Eventual => "Eventual",
    }
}

fn option_string(value: Option<String>) -> Value {
    value.map(Value::String).unwrap_or(Value::Null)
}

fn path_to_value(path: &Option<PathBuf>) -> Value {
    path.as_ref()
        .map(|path| Value::String(path.to_string_lossy().to_string()))
        .unwrap_or(Value::Null)
}

fn directory_size(path: &Path) -> u64 {
    let Ok(entries) = std::fs::read_dir(path) else {
        return 0;
    };
    entries
        .filter_map(Result::ok)
        .map(|entry| {
            let path = entry.path();
            match entry.metadata() {
                Ok(metadata) if metadata.is_file() => metadata.len(),
                Ok(metadata) if metadata.is_dir() => directory_size(&path),
                _ => 0,
            }
        })
        .sum()
}
