//! `cosmos-emulator` CLI. Ports the .NET `Cli` project (System.CommandLine).
//!
//! Subcommands mirror the original: `start | stop | reset | status | export | import`.
//! Only `start` is wired to boot the host in this scaffold; the rest are stubs
//! completed during the `cli-crate` phase of the roadmap.

use std::path::PathBuf;

use clap::{Parser, Subcommand, ValueEnum};
use cosmos_host::{HostOptions, DEFAULT_PORT};

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
        /// Storage backend to use.
        #[arg(long, value_enum, default_value_t = Storage::Sqlite)]
        storage: Storage,
        /// Directory for persistent data.
        #[arg(long)]
        data_dir: Option<PathBuf>,
        /// NoSQL REST API port.
        #[arg(long, default_value_t = DEFAULT_PORT)]
        port: u16,
        /// Directory of the built Explorer SPA to serve at /explorer.
        #[arg(long)]
        explorer_dir: Option<PathBuf>,
    },
    /// Stop a running emulator.
    Stop,
    /// Reset all emulator data.
    Reset,
    /// Show emulator status.
    Status,
    /// Export data to a file.
    Export {
        #[arg(long)]
        output: PathBuf,
    },
    /// Import data from a file.
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

#[tokio::main]
async fn main() -> Result<(), anyhow::Error> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()),
        )
        .init();

    let cli = Cli::parse();
    match cli.command {
        Command::Start {
            storage: _,
            data_dir: _,
            port,
            explorer_dir,
        } => {
            cosmos_host::run(HostOptions { port, explorer_dir }).await?;
        }
        Command::Stop => println!("stop: not yet implemented"),
        Command::Reset => println!("reset: not yet implemented"),
        Command::Status => println!("status: not yet implemented"),
        Command::Export { output } => {
            println!("export to {}: not yet implemented", output.display())
        }
        Command::Import { input } => {
            println!("import from {}: not yet implemented", input.display())
        }
    }
    Ok(())
}
