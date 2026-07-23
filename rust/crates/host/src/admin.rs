use std::path::PathBuf;
use std::sync::Arc;

use axum::extract::State;
use axum::http::{HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use axum::{Json, Router};
use cosmos_core::models::headers as h;
use cosmos_core::StorageType;
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

use crate::HostOptions;

const ADMIN_SETTINGS_FILE: &str = "admin-settings.json";
const EMULATOR_CONFIG_FILE: &str = "emulator-config.json";

#[derive(Clone)]
pub struct AdminSettingsStore {
    inner: Arc<AdminSettingsStoreInner>,
}

struct AdminSettingsStoreInner {
    path: PathBuf,
    default_enable_entra: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdminSettings {
    pub enable_entra_id: bool,
    pub tenant_id: Option<String>,
    pub client_id: Option<String>,
}

impl AdminSettingsStore {
    pub fn new(data_dir: Option<PathBuf>, default_enable_entra: bool) -> Self {
        let data_dir = data_dir.unwrap_or_else(|| PathBuf::from("./cosmos-data"));
        Self {
            inner: Arc::new(AdminSettingsStoreInner {
                path: data_dir.join(ADMIN_SETTINGS_FILE),
                default_enable_entra,
            }),
        }
    }

    pub fn get_stored_settings(&self) -> Option<AdminSettings> {
        let text = std::fs::read_to_string(&self.inner.path).ok()?;
        serde_json::from_str(&text).ok()
    }

    pub fn get_effective_settings(&self) -> AdminSettings {
        let stored = self.get_stored_settings();
        AdminSettings {
            enable_entra_id: stored
                .as_ref()
                .map(|settings| settings.enable_entra_id)
                .unwrap_or(self.inner.default_enable_entra),
            tenant_id: normalize(
                stored
                    .as_ref()
                    .and_then(|settings| settings.tenant_id.clone()),
            ),
            client_id: normalize(
                stored
                    .as_ref()
                    .and_then(|settings| settings.client_id.clone()),
            ),
        }
    }

    pub fn update_settings(
        &self,
        enable_entra_id: bool,
        tenant_id: Option<&str>,
        client_id: Option<&str>,
    ) -> std::io::Result<AdminSettings> {
        let settings = AdminSettings {
            enable_entra_id,
            tenant_id: normalize(tenant_id.map(str::to_string)),
            client_id: normalize(client_id.map(str::to_string)),
        };
        if let Some(parent) = self.inner.path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_vec_pretty(&settings).map_err(std::io::Error::other)?;
        std::fs::write(&self.inner.path, json)?;
        Ok(settings)
    }
}

#[derive(Clone)]
pub struct AdminConfigState {
    runtime: Arc<RuntimeConfig>,
    config_store: ConfigStore,
}

#[derive(Clone)]
struct RuntimeConfig {
    storage: String,
    data_dir: String,
}

#[derive(Clone)]
struct ConfigStore {
    path: Arc<PathBuf>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct EmulatorConfigRequest {
    storage: Option<String>,
    data_directory: Option<String>,
}

impl AdminConfigState {
    pub fn new(opts: &HostOptions) -> Self {
        let data_dir = opts
            .data_dir
            .clone()
            .unwrap_or_else(|| PathBuf::from("./cosmos-data"));
        Self {
            runtime: Arc::new(RuntimeConfig {
                storage: storage_name(opts.storage).to_string(),
                data_dir: data_dir.to_string_lossy().to_string(),
            }),
            config_store: ConfigStore {
                path: Arc::new(data_dir.join(EMULATOR_CONFIG_FILE)),
            },
        }
    }
}

pub fn router(state: AdminConfigState) -> Router {
    Router::new()
        .route(
            "/api/emulator/config",
            get(get_config).put(update_config).post(update_config),
        )
        .with_state(state)
}

async fn get_config(State(state): State<AdminConfigState>) -> Response {
    let file_config = state.config_store.read();
    let response = config_response(&state.runtime, file_config.as_ref(), false);
    ok(response)
}

async fn update_config(
    State(state): State<AdminConfigState>,
    Json(request): Json<EmulatorConfigRequest>,
) -> Response {
    let mut file_config = state.config_store.read().unwrap_or_default();
    let emulator_section = emulator_section_mut(&mut file_config);

    if let Some(storage) = request.storage.as_ref() {
        emulator_section.insert("Storage".into(), Value::String(storage.clone()));
    }
    if let Some(data_directory) = request.data_directory.as_ref() {
        emulator_section.insert(
            "DataDirectory".into(),
            Value::String(data_directory.clone()),
        );
    }

    if let Err(error) = state.config_store.write(&file_config) {
        return internal_error(format!("Failed to persist emulator config: {error}"));
    }

    let restart_required = request
        .storage
        .as_ref()
        .is_some_and(|storage| !storage.eq_ignore_ascii_case(&state.runtime.storage))
        || request
            .data_directory
            .as_ref()
            .is_some_and(|dir| !dir.eq_ignore_ascii_case(&state.runtime.data_dir));

    ok(config_response(
        &state.runtime,
        Some(&file_config),
        restart_required,
    ))
}

impl ConfigStore {
    fn read(&self) -> Option<Map<String, Value>> {
        let text = std::fs::read_to_string(self.path.as_ref()).ok()?;
        serde_json::from_str::<Value>(&text)
            .ok()?
            .as_object()
            .cloned()
    }

    fn write(&self, document: &Map<String, Value>) -> std::io::Result<()> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_vec_pretty(document).map_err(std::io::Error::other)?;
        std::fs::write(self.path.as_ref(), json)
    }
}

fn config_response(
    runtime: &RuntimeConfig,
    file_config: Option<&Map<String, Value>>,
    restart_required: bool,
) -> Value {
    let emulator = file_config
        .and_then(|config| config.get("Emulator"))
        .and_then(Value::as_object);
    let storage = emulator
        .and_then(|section| section.get("Storage"))
        .and_then(Value::as_str)
        .unwrap_or(&runtime.storage);
    let data_directory = emulator
        .and_then(|section| section.get("DataDirectory"))
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .unwrap_or(&runtime.data_dir);

    json!({
        "storage": storage,
        "dataDirectory": data_directory,
        "restartRequired": restart_required,
    })
}

fn emulator_section_mut(config: &mut Map<String, Value>) -> &mut Map<String, Value> {
    let needs_section = !config.get("Emulator").is_some_and(Value::is_object);
    if needs_section {
        config.insert("Emulator".into(), Value::Object(Map::new()));
    }
    config
        .get_mut("Emulator")
        .and_then(Value::as_object_mut)
        .expect("Emulator section must be an object")
}

fn ok(body: Value) -> Response {
    (StatusCode::OK, common_headers(), Json(body)).into_response()
}

fn internal_error(message: String) -> Response {
    let body = json!({ "code": "InternalServerError", "message": message });
    (
        StatusCode::INTERNAL_SERVER_ERROR,
        common_headers(),
        Json(body),
    )
        .into_response()
}

fn common_headers() -> HeaderMap {
    let mut headers = HeaderMap::new();
    insert_header(&mut headers, h::REQUEST_CHARGE, "1.00");
    insert_header(
        &mut headers,
        h::ACTIVITY_ID,
        uuid::Uuid::new_v4().to_string(),
    );
    insert_header(&mut headers, h::SERVICE_VERSION, h::CURRENT_SERVICE_VERSION);
    headers
}

fn insert_header(headers: &mut HeaderMap, name: &'static str, value: impl ToString) {
    if let Ok(value) = HeaderValue::from_str(&value.to_string()) {
        headers.insert(name, value);
    }
}

fn normalize(value: Option<String>) -> Option<String> {
    value
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

fn storage_name(storage: StorageType) -> &'static str {
    match storage {
        StorageType::SurrealDb => "SurrealDb",
        StorageType::Sqlite => "Sqlite",
        StorageType::InMemory => "InMemory",
    }
}
