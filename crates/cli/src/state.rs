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

/// The platform local-application-data directory.
#[cfg(target_os = "windows")]
fn local_app_data() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .filter(|dir| !dir.is_empty())
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
}

#[cfg(target_os = "macos")]
fn local_app_data() -> PathBuf {
    std::env::var_os("HOME")
        .filter(|dir| !dir.is_empty())
        .map(PathBuf::from)
        .map(|home| home.join("Library").join("Application Support"))
        .unwrap_or_else(|| PathBuf::from("."))
}

#[cfg(all(unix, not(target_os = "macos")))]
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

#[cfg(unix)]
pub fn current_process_started_at_utc() -> Option<DateTime<Utc>> {
    Some(Utc::now())
}

#[cfg(target_os = "windows")]
pub fn current_process_started_at_utc() -> Option<DateTime<Utc>> {
    use windows_sys::Win32::System::Threading::GetCurrentProcess;

    process_started_at_utc(unsafe { GetCurrentProcess() })
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

/// Returns `true` if the recorded process is still alive.
#[cfg(unix)]
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

#[cfg(target_os = "windows")]
pub fn is_process_running(state: &EmulatorInstanceState) -> bool {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, PROCESS_QUERY_LIMITED_INFORMATION,
    };

    let Some(handle) = open_matching_process(state, PROCESS_QUERY_LIMITED_INFORMATION) else {
        return false;
    };
    let mut exit_code = 0;
    let running = unsafe { GetExitCodeProcess(handle, &mut exit_code) } != 0 && exit_code == 259;
    unsafe {
        CloseHandle(handle);
    }
    running
}

#[cfg(target_os = "windows")]
fn process_started_at_utc(
    process: windows_sys::Win32::Foundation::HANDLE,
) -> Option<DateTime<Utc>> {
    use windows_sys::Win32::Foundation::FILETIME;
    use windows_sys::Win32::System::Threading::GetProcessTimes;

    let mut creation = FILETIME {
        dwLowDateTime: 0,
        dwHighDateTime: 0,
    };
    let mut exit = creation;
    let mut kernel = creation;
    let mut user = creation;
    if unsafe { GetProcessTimes(process, &mut creation, &mut exit, &mut kernel, &mut user) } == 0 {
        return None;
    }
    let ticks = ((creation.dwHighDateTime as u64) << 32) | creation.dwLowDateTime as u64;
    let unix_ticks = ticks.checked_sub(116_444_736_000_000_000)?;
    let seconds = (unix_ticks / 10_000_000) as i64;
    let nanoseconds = ((unix_ticks % 10_000_000) * 100) as u32;
    DateTime::from_timestamp(seconds, nanoseconds)
}

#[cfg(target_os = "windows")]
fn open_matching_process(
    state: &EmulatorInstanceState,
    access: u32,
) -> Option<windows_sys::Win32::Foundation::HANDLE> {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::OpenProcess;

    if state.process_id <= 0 {
        return None;
    }
    let expected_started_at = state.process_started_at_utc?;
    let handle = unsafe { OpenProcess(access, 0, state.process_id as u32) };
    if handle.is_null() {
        return None;
    }
    if process_started_at_utc(handle) != Some(expected_started_at) {
        unsafe {
            CloseHandle(handle);
        }
        return None;
    }
    Some(handle)
}

/// Requests that the recorded process terminate.
#[cfg(unix)]
pub fn terminate_process(state: &EmulatorInstanceState) {
    unsafe {
        libc::kill(state.process_id, libc::SIGTERM);
    }
}

#[cfg(target_os = "windows")]
pub fn terminate_process(state: &EmulatorInstanceState) {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::{
        TerminateProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_TERMINATE,
    };

    let Some(handle) =
        open_matching_process(state, PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE)
    else {
        return;
    };
    unsafe {
        TerminateProcess(handle, 1);
        CloseHandle(handle);
    }
}
