//! `cosmos-emulator` CLI. Ports the .NET `Cli` project (System.CommandLine).
//!
//! Subcommands mirror the original: `start | stop | reset | status | export |
//! import`. `start` boots the Axum host (optionally in the background) and
//! records instance state so the lifecycle commands can find it.

mod client;
mod state;

use std::net::TcpListener;
use std::path::PathBuf;
use std::process::Stdio;

use clap::{Parser, Subcommand, ValueEnum};
use cosmos_host::{HostOptions, DEFAULT_PORT};
use state::{
    cleanup_state_files, default_data_dir, is_process_running, persist_state, terminate_process,
    try_load_current_state, EmulatorInstanceState, DEFAULT_CONSISTENCY,
};

const DEFAULT_MONGO_PORT: u16 = 10255;

#[derive(Parser)]
#[command(
    name = "cosmos-emulator",
    version,
    about = "Azure Cosmos DB light emulator"
)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Start the emulator (NoSQL REST API + MongoDB wire protocol).
    Start {
        /// NoSQL REST API port.
        #[arg(long, default_value_t = DEFAULT_PORT)]
        port: u16,
        /// MongoDB wire-protocol port.
        #[arg(long, default_value_t = DEFAULT_MONGO_PORT)]
        mongo_port: u16,
        /// Directory for persistent data.
        #[arg(long)]
        data_dir: Option<PathBuf>,
        /// Master key to use for authentication.
        #[arg(long)]
        key: Option<String>,
        /// Enable Entra ID authentication support.
        #[arg(long)]
        enable_entra: bool,
        /// Default consistency level.
        #[arg(long, default_value = DEFAULT_CONSISTENCY)]
        consistency: String,
        /// Enable verbose logging.
        #[arg(long)]
        verbose: bool,
        /// Storage backend to use.
        #[arg(long, value_enum, default_value_t = Storage::Sqlite)]
        storage: Storage,
        /// Run the emulator in the background.
        #[arg(long)]
        background: bool,
        /// Directory of the built Explorer SPA to serve at /explorer.
        #[arg(long)]
        explorer_dir: Option<PathBuf>,
        /// Internal: bootstrap the host process (used by background start).
        #[arg(long, hide = true)]
        run_host_internal: bool,
    },
    /// Stop a running background emulator instance.
    Stop,
    /// Reset all emulator data.
    Reset,
    /// Show emulator status and connection details.
    Status,
    /// Export data to a JSON file.
    Export {
        #[arg(long)]
        output: PathBuf,
    },
    /// Import data from a JSON file.
    Import {
        #[arg(long)]
        input: PathBuf,
    },
}

/// Storage backends, matching the .NET `StorageType` (Sqlite is the default).
#[derive(Clone, Copy, ValueEnum)]
enum Storage {
    Sqlite,
    SurrealDb,
    InMemory,
}

impl Storage {
    fn as_arg(&self) -> &'static str {
        match self {
            Storage::Sqlite => "sqlite",
            Storage::SurrealDb => "surreal-db",
            Storage::InMemory => "in-memory",
        }
    }
}

impl From<Storage> for cosmos_core::StorageType {
    fn from(value: Storage) -> Self {
        match value {
            Storage::Sqlite => cosmos_core::StorageType::Sqlite,
            Storage::SurrealDb => cosmos_core::StorageType::SurrealDb,
            Storage::InMemory => cosmos_core::StorageType::InMemory,
        }
    }
}

#[tokio::main]
async fn main() -> Result<(), anyhow::Error> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .init();

    let cli = Cli::parse();
    let code = match cli.command {
        Command::Start {
            port,
            mongo_port,
            data_dir,
            key,
            enable_entra,
            consistency,
            verbose,
            storage,
            background,
            explorer_dir,
            run_host_internal,
        } => {
            start(
                StartArgs {
                    port,
                    mongo_port,
                    data_dir,
                    key,
                    enable_entra,
                    consistency,
                    verbose,
                    storage,
                    background,
                    explorer_dir,
                },
                run_host_internal,
            )
            .await?
        }
        Command::Stop => stop().await,
        Command::Reset => reset().await,
        Command::Status => status().await,
        Command::Export { output } => export(output).await?,
        Command::Import { input } => import(input).await?,
    };
    std::process::exit(code);
}

struct StartArgs {
    port: u16,
    mongo_port: u16,
    data_dir: Option<PathBuf>,
    key: Option<String>,
    enable_entra: bool,
    consistency: String,
    verbose: bool,
    storage: Storage,
    background: bool,
    explorer_dir: Option<PathBuf>,
}

async fn start(args: StartArgs, run_host_internal: bool) -> Result<i32, anyhow::Error> {
    let data_dir = args
        .data_dir
        .clone()
        .unwrap_or_else(default_data_dir)
        .canonicalize_or_self();
    let master_key = args
        .key
        .clone()
        .filter(|k| !k.is_empty())
        .unwrap_or_else(|| cosmos_auth::DEFAULT_MASTER_KEY.to_string());
    std::fs::create_dir_all(&data_dir)?;

    // Refuse to start a second instance.
    if let Some(existing) = try_load_current_state() {
        if is_process_running(&existing) {
            println!("Emulator is already running (PID {}).", existing.process_id);
            println!("Endpoint: {}", existing.endpoint());
            return Ok(1);
        }
        cleanup_state_files(&existing.data_directory);
    }

    println!("Checking port availability...");
    let (port, mongo_port) = resolve_available_ports(args.port, args.mongo_port);

    if args.background && !run_host_internal {
        return start_background(&args, &data_dir, &master_key, port, mongo_port).await;
    }

    // Foreground / internal host: persist state, then serve.
    let state = EmulatorInstanceState {
        process_id: std::process::id() as i32,
        process_started_at_utc: Some(chrono::Utc::now()),
        port,
        mongo_port,
        data_directory: data_dir.to_string_lossy().to_string(),
        master_key,
        enable_entra_id: args.enable_entra,
        consistency_level: args.consistency.clone(),
        verbose: args.verbose,
        enable_ssl: false,
    };
    persist_state(&state)?;

    let data_directory = state.data_directory.clone();
    let result = cosmos_host::run(HostOptions {
        port,
        storage: args.storage.into(),
        data_dir: Some(data_dir),
        explorer_dir: args.explorer_dir.clone(),
        master_key: Some(state.master_key.clone()),
        consistency: cosmos_core::ConsistencyLevel::parse(&state.consistency_level),
    })
    .await;
    cleanup_state_files(&data_directory);
    result?;
    Ok(0)
}

async fn start_background(
    args: &StartArgs,
    data_dir: &std::path::Path,
    master_key: &str,
    port: u16,
    mongo_port: u16,
) -> Result<i32, anyhow::Error> {
    let exe = std::env::current_exe()?;
    let mut cmd = std::process::Command::new(exe);
    cmd.arg("start")
        .arg("--run-host-internal")
        .arg("--port")
        .arg(port.to_string())
        .arg("--mongo-port")
        .arg(mongo_port.to_string())
        .arg("--data-dir")
        .arg(data_dir)
        .arg("--consistency")
        .arg(&args.consistency)
        .arg("--storage")
        .arg(args.storage.as_arg());
    if !master_key.is_empty() {
        cmd.arg("--key").arg(master_key);
    }
    if args.enable_entra {
        cmd.arg("--enable-entra");
    }
    if args.verbose {
        cmd.arg("--verbose");
    }
    if let Some(dir) = &args.explorer_dir {
        cmd.arg("--explorer-dir").arg(dir);
    }
    cmd.stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    let child = cmd.spawn()?;
    let child_pid = child.id() as i32;

    // Wait for the background instance to become healthy.
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
    while std::time::Instant::now() < deadline {
        if let Some(state) = try_load_current_state() {
            if state.process_id == child_pid && client::is_endpoint_healthy(&state).await {
                print_connection_info(&state, true);
                return Ok(0);
            }
        }
        tokio::time::sleep(std::time::Duration::from_millis(250)).await;
    }

    eprintln!("Timed out waiting for the emulator to start.");
    Ok(1)
}

async fn stop() -> i32 {
    let Some(state) = try_load_current_state() else {
        println!("Emulator is not running.");
        return 0;
    };
    if !is_process_running(&state) {
        cleanup_state_files(&state.data_directory);
        println!("Emulator is not running.");
        return 0;
    }
    terminate_process(state.process_id);
    // Give the process a moment to exit.
    for _ in 0..40 {
        if !is_process_running(&state) {
            break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(100)).await;
    }
    cleanup_state_files(&state.data_directory);
    println!("Stopped emulator process {}.", state.process_id);
    0
}

async fn reset() -> i32 {
    let state = try_load_current_state();
    if let Some(state) = &state {
        if is_process_running(state) {
            let code = stop().await;
            if code != 0 {
                return code;
            }
        }
    }
    let data_dir = state
        .map(|s| PathBuf::from(s.data_directory))
        .unwrap_or_else(default_data_dir);
    if data_dir.exists() {
        let _ = std::fs::remove_dir_all(&data_dir);
    }
    let _ = std::fs::create_dir_all(&data_dir);
    cleanup_state_files(&data_dir.to_string_lossy());
    println!("Reset emulator data directory: {}", data_dir.display());
    0
}

async fn status() -> i32 {
    let Some(state) = try_load_current_state() else {
        println!("Emulator is not running.");
        return 0;
    };
    let running = is_process_running(&state) && client::is_endpoint_healthy(&state).await;
    if !running {
        cleanup_state_files(&state.data_directory);
        println!("Emulator is not running.");
        return 0;
    }
    print_connection_info(&state, true);
    0
}

async fn require_running_state() -> Option<EmulatorInstanceState> {
    let state = try_load_current_state()?;
    if !is_process_running(&state) || !client::is_endpoint_healthy(&state).await {
        cleanup_state_files(&state.data_directory);
        eprintln!("Emulator is not running.");
        return None;
    }
    Some(state)
}

async fn export(output: PathBuf) -> Result<i32, anyhow::Error> {
    let Some(state) = require_running_state().await else {
        return Ok(1);
    };
    let document = client::export(&state).await?;
    let full = std::path::absolute(&output).unwrap_or(output);
    if let Some(parent) = full.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&full, serde_json::to_string_pretty(&document)?)?;
    println!("Exported emulator data to {}", full.display());
    Ok(0)
}

async fn import(input: PathBuf) -> Result<i32, anyhow::Error> {
    let full = std::path::absolute(&input).unwrap_or(input);
    if !full.exists() {
        eprintln!("Input file was not found: {}", full.display());
        return Ok(1);
    }
    let Some(state) = require_running_state().await else {
        return Ok(1);
    };
    let document: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(&full)?)?;
    client::import(&state, &document).await?;
    println!("Imported emulator data from {}", full.display());
    Ok(0)
}

/// Finds the next free TCP port at or above `start` on all interfaces.
fn next_free_port(start: u16) -> u16 {
    let mut port = start;
    for _ in 0..100 {
        if TcpListener::bind(("0.0.0.0", port)).is_ok() {
            return port;
        }
        port = port.saturating_add(1).max(1);
    }
    start
}

fn resolve_available_ports(port: u16, mongo_port: u16) -> (u16, u16) {
    let nosql = next_free_port(port);
    let mut mongo = next_free_port(mongo_port);
    if mongo == nosql {
        mongo = next_free_port(nosql.saturating_add(1));
    }
    (nosql, mongo)
}

fn print_connection_info(state: &EmulatorInstanceState, include_status: bool) {
    let mongo_endpoint = format!("mongodb://localhost:{}", state.mongo_port);
    let connection_string = format!(
        "AccountEndpoint={};AccountKey={};",
        state.endpoint(),
        state.master_key
    );

    const INNER: usize = 108;
    let border = "═".repeat(INNER);
    println!();
    println!("╔{border}╗");
    println!("║{:<INNER$}║", "  Azure Cosmos DB Light Emulator");
    println!("╠{border}╣");
    if include_status {
        println!(
            "║  Status:           {:<88}║",
            format!("Running (PID {})", state.process_id)
        );
    }
    println!("║  NoSQL Endpoint:   {:<88}║", state.endpoint());
    println!("║  MongoDB Endpoint: {:<88}║", mongo_endpoint);
    println!("║  Consistency:      {:<88}║", state.consistency_level);
    println!("╠{border}╣");
    println!("║{:<INNER$}║", "  Master Key:");
    println!("║    {:<104}║", state.master_key);
    println!("╠{border}╣");
    println!("║{:<INNER$}║", "  Connection String:");
    let chars: Vec<char> = connection_string.chars().collect();
    for chunk in chars.chunks(104) {
        let s: String = chunk.iter().collect();
        println!("║    {s:<104}║");
    }
    println!("╚{border}╝");
    println!();
}

/// Extension to canonicalize a path, falling back to the original if it does
/// not yet exist.
trait CanonicalizeOrSelf {
    fn canonicalize_or_self(self) -> PathBuf;
}

impl CanonicalizeOrSelf for PathBuf {
    fn canonicalize_or_self(self) -> PathBuf {
        std::fs::canonicalize(&self).unwrap_or(self)
    }
}
