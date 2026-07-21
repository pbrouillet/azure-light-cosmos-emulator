//! MongoDB wire-protocol server. Ports the .NET `MongoDB` project.
//!
//! A `tokio` TCP server on port 10255 that speaks the MongoDB wire protocol
//! (`OP_MSG`, `OP_QUERY`, `OP_REPLY`). The message framing and command dispatch
//! port `MongoDbServer`/`MongoDbConnectionHandler`, improved to use the `bson`
//! crate for correct document (de)serialization (the .NET stub wrapped raw JSON
//! bytes as pseudo-BSON) and to recognise the standard handshake/diagnostic
//! commands so a real driver can complete its connection handshake.
//!
//! **Gap vs full MongoDB:** like the .NET emulator, document CRUD commands
//! (`insert`/`find`/`update`/`delete`) against the shared storage layer are not
//! yet implemented — they currently return `{ ok: 1 }`. Wiring these to the
//! `DocumentStore` is future work (tracked alongside the parity phase).

pub mod commands;
pub mod server;
pub mod wire;

pub use server::MongoDbServer;

/// Default MongoDB wire-protocol port, matching the .NET emulator.
pub const DEFAULT_MONGO_PORT: u16 = 10255;

/// Binds and runs the MongoDB wire-protocol listener on `port` until cancelled.
pub async fn serve(port: u16) -> Result<(), anyhow::Error> {
    let server = MongoDbServer::bind(port).await?;
    server.run().await?;
    Ok(())
}
