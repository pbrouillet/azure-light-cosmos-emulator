//! Emulator instance state: on-disk tracking of a running background emulator.
//!
//! Ports the state-file handling from the .NET `Cli/Program.cs`
//! (`EmulatorInstanceState`, `CurrentInstancePointer`, and the global state
//! directory under the platform's local-application-data folder).

use std::fs;
use std::path::{Path, PathBuf};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

pub const DEFAULT_CONSISTENCY: &str = "Session";
const STATE_FILE_NAME: &str = "emulator-instance.json";
const PID_FILE_NAME: &str = "emulator.pid";

/// Persisted description of a running emulator instance.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EmulatorInstanceState {
    pub process_id: i32,
    #[serde(default)]
    pub process_started_at_utc: Option<DateTime<Utc>>,
    pub port: u16,
    pub mongo_port: u16,
    pub data_directory: String,
    pub master_key: String,
    #[serde(default)]
    pub enable_entra_id: bool,
    #[serde(default = "default_consistency")]
    pub consistency_level: String,
    #[serde(default)]
    pub verbose: bool,
    #[serde(default)]
    pub enable_ssl: bool,
}

fn default_consistency() -> String {
    DEFAULT_CONSISTENCY.to_string()
}

impl EmulatorInstanceState {
    /// The base HTTP(S) endpoint URL.
    pub fn endpoint(&self) -> String {
        let scheme = if self.enable_ssl { "https" } else { "http" };
        format!("{scheme}://localhost:{}", self.port)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CurrentInstancePointer {
    pub data_directory: String,
}

/// The platform local-application-data directory (matches .NET
/// `Environment.SpecialFolder.LocalApplicationData`).
fn local_app_data() -> PathBuf {
    if let Ok(dir) = std::env::var("XDG_DATA_HOME") {
        if !dir.is_empty() {
            return PathBuf::from(dir);
        }
    }
    if let Ok(home) = std::env::var("HOME") {
        return PathBuf::from(home).join(".local").join("share");
    }
    PathBuf::from(".")
}

/// `<local-app-data>/CosmosEmulator`.
pub fn global_state_dir() -> PathBuf {
    local_app_data().join("CosmosEmulator")
}

/// The default data directory: `<global-state-dir>/data`.
pub fn default_data_dir() -> PathBuf {
    global_state_dir().join("data")
}

fn current_instance_file() -> PathBuf {
    global_state_dir().join("current-instance.json")
}

fn pid_file(data_dir: &Path) -> PathBuf {
    data_dir.join(PID_FILE_NAME)
}

fn state_file(data_dir: &Path) -> PathBuf {
    data_dir.join(STATE_FILE_NAME)
}

fn try_load<T: for<'de> Deserialize<'de>>(path: &Path) -> Option<T> {
    let text = fs::read_to_string(path).ok()?;
    serde_json::from_str(&text).ok()
}

/// Loads the state for the current instance, checking the current-instance
/// pointer then the default data directory.
pub fn try_load_current_state() -> Option<EmulatorInstanceState> {
    let pointer: Option<CurrentInstancePointer> = try_load(&current_instance_file());
    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Some(p) = pointer {
        candidates.push(PathBuf::from(p.data_directory));
    }
    candidates.push(default_data_dir());

    for dir in candidates {
        if let Some(state) = try_load::<EmulatorInstanceState>(&state_file(&dir)) {
            return Some(state);
        }
    }
    None
}

/// Persists the instance state files (pid, state, current pointer).
pub fn persist_state(state: &EmulatorInstanceState) -> std::io::Result<()> {
    let data_dir = PathBuf::from(&state.data_directory);
    fs::create_dir_all(&data_dir)?;
    fs::create_dir_all(global_state_dir())?;

    fs::write(pid_file(&data_dir), state.process_id.to_string())?;
    fs::write(
        state_file(&data_dir),
        serde_json::to_string_pretty(state).unwrap_or_default(),
    )?;
    let pointer = CurrentInstancePointer {
        data_directory: state.data_directory.clone(),
    };
    fs::write(
        current_instance_file(),
        serde_json::to_string_pretty(&pointer).unwrap_or_default(),
    )?;
    Ok(())
}

/// Removes the instance's state files.
pub fn cleanup_state_files(data_directory: &str) {
    if !data_directory.is_empty() {
        let dir = PathBuf::from(data_directory);
        let _ = fs::remove_file(pid_file(&dir));
        let _ = fs::remove_file(state_file(&dir));
    }
    let current: Option<CurrentInstancePointer> = try_load(&current_instance_file());
    match current {
        Some(c) if c.data_directory == data_directory => {
            let _ = fs::remove_file(current_instance_file());
        }
        None => {
            let _ = fs::remove_file(current_instance_file());
        }
        _ => {}
    }
}

/// Returns `true` if the recorded process is still alive (Unix: signal 0 probe).
pub fn is_process_running(state: &EmulatorInstanceState) -> bool {
    if state.process_id <= 0 {
        return false;
    }
    // Sending signal 0 checks for the existence of the process without
    // affecting it; success (or EPERM) means it is alive.
    let res = unsafe { libc::kill(state.process_id, 0) };
    if res == 0 {
        return true;
    }
    std::io::Error::last_os_error().raw_os_error() == Some(libc::EPERM)
}

/// Sends SIGTERM to the recorded process.
pub fn terminate_process(pid: i32) {
    unsafe {
        libc::kill(pid, libc::SIGTERM);
    }
}
