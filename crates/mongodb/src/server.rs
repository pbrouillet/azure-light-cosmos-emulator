//! TCP server and per-connection handler for the MongoDB wire protocol.
//!
//! Ports `MongoDbServer` + `MongoDbConnectionHandler` onto `tokio`. Each
//! accepted connection is handled concurrently; messages are framed by the
//! 16-byte header and dispatched by op-code (`OP_MSG`/`OP_QUERY`).

use std::io;
use std::net::SocketAddr;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};

use crate::commands;
use crate::wire::{self, MsgHeader};

/// Matches the `maxMessageSizeBytes` advertised in the handshake. Guards against
/// a malformed/hostile length field triggering a huge allocation.
const MAX_MESSAGE_LENGTH: i32 = 48_000_000;

/// A running MongoDB wire-protocol server bound to a TCP port.
pub struct MongoDbServer {
    listener: TcpListener,
    local_addr: SocketAddr,
}

impl MongoDbServer {
    /// Binds the server to `0.0.0.0:port`. A `port` of 0 selects an ephemeral
    /// port (useful for tests); read it back via [`MongoDbServer::local_addr`].
    pub async fn bind(port: u16) -> io::Result<Self> {
        let listener = TcpListener::bind((std::net::Ipv4Addr::UNSPECIFIED, port)).await?;
        let local_addr = listener.local_addr()?;
        tracing::info!(
            port = local_addr.port(),
            "MongoDB wire protocol server listening"
        );
        Ok(Self {
            listener,
            local_addr,
        })
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.local_addr
    }

    /// Runs the accept loop until the future is dropped/cancelled. Each
    /// connection is served on its own task.
    pub async fn run(self) -> io::Result<()> {
        loop {
            match self.listener.accept().await {
                Ok((stream, peer)) => {
                    tracing::debug!(%peer, "MongoDB client connected");
                    tokio::spawn(async move {
                        if let Err(e) = handle_connection(stream).await {
                            if e.kind() != io::ErrorKind::UnexpectedEof {
                                tracing::error!(error = %e, "MongoDB connection error");
                            }
                        }
                    });
                }
                Err(e) => {
                    tracing::error!(error = %e, "error accepting MongoDB connection");
                }
            }
        }
    }
}

/// Reads exactly `buf.len()` bytes, returning the number read. Returns 0 on a
/// clean EOF at a message boundary.
async fn read_exact_or_eof(stream: &mut TcpStream, buf: &mut [u8]) -> io::Result<usize> {
    let mut total = 0;
    while total < buf.len() {
        let n = stream.read(&mut buf[total..]).await?;
        if n == 0 {
            return Ok(total);
        }
        total += n;
    }
    Ok(total)
}

/// Handles one client connection: frames messages and dispatches them.
pub async fn handle_connection(mut stream: TcpStream) -> io::Result<()> {
    let mut header_buf = [0u8; MsgHeader::SIZE];
    loop {
        let read = read_exact_or_eof(&mut stream, &mut header_buf).await?;
        if read == 0 {
            return Ok(()); // clean close at a message boundary
        }
        if read < MsgHeader::SIZE {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "truncated message header",
            ));
        }

        let header = MsgHeader::parse(&header_buf);
        tracing::debug!(
            length = header.message_length,
            request_id = header.request_id,
            op_code = header.op_code,
            "MongoDB message"
        );

        if header.message_length < MsgHeader::SIZE as i32
            || header.message_length > MAX_MESSAGE_LENGTH
        {
            tracing::warn!(
                length = header.message_length,
                "MongoDB message length out of range; closing connection"
            );
            return Ok(());
        }

        let body_len = (header.message_length as usize) - MsgHeader::SIZE;
        let mut body = vec![0u8; body_len];
        if body_len > 0 {
            let n = read_exact_or_eof(&mut stream, &mut body).await?;
            if n < body_len {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "connection closed mid-message",
                ));
            }
        }

        let response = build_response(&header, &body);
        stream.write_all(&response).await?;
        stream.flush().await?;
    }
}

/// Builds the wire response bytes for a single request.
fn build_response(header: &MsgHeader, body: &[u8]) -> Vec<u8> {
    match header.op_code {
        wire::OP_MSG => match wire::parse_op_msg(header.request_id, body) {
            Ok(parsed) => {
                let reply = commands::dispatch(&parsed.command);
                wire::encode_op_msg(header.request_id, &reply)
            }
            Err(e) => wire::encode_op_msg(
                header.request_id,
                &commands::error_response(&format!("failed to parse OP_MSG: {e}"), 9),
            ),
        },
        wire::OP_QUERY => match wire::parse_op_query(header.request_id, body) {
            // Legacy handshake path: always answer with an isMaster/hello reply.
            Ok(_) => wire::encode_op_reply(header.request_id, &commands::hello_response()),
            Err(e) => wire::encode_op_reply(
                header.request_id,
                &commands::error_response(&format!("failed to parse OP_QUERY: {e}"), 9),
            ),
        },
        other => wire::encode_op_msg(
            header.request_id,
            &commands::error_response(&format!("Unsupported opCode: {other}"), 9),
        ),
    }
}
