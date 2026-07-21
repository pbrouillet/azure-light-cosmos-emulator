//! MongoDB wire-protocol server. Ports the .NET `MongoDB` project.
//!
//! A `tokio` TCP server on port 10255 that speaks the MongoDB wire protocol
//! (OP_MSG, OP_QUERY, OP_REPLY), backed by the shared storage layer.

/// Default MongoDB wire-protocol port, matching the .NET emulator.
pub const DEFAULT_MONGO_PORT: u16 = 10255;

/// Starts the MongoDB wire-protocol listener (stub).
pub async fn serve(_port: u16) -> Result<(), anyhow::Error> {
    // Filled in during the `mongodb-crate` phase.
    Ok(())
}
