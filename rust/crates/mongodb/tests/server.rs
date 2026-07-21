//! End-to-end integration tests: start the server on an ephemeral port and
//! exercise the wire protocol over a real TCP socket.

use bson::{doc, Document};
use cosmos_mongodb::wire::{self, MsgHeader};
use cosmos_mongodb::MongoDbServer;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;

async fn read_message(stream: &mut TcpStream) -> (MsgHeader, Vec<u8>) {
    let mut header_buf = [0u8; 16];
    stream.read_exact(&mut header_buf).await.unwrap();
    let header = MsgHeader::parse(&header_buf);
    let body_len = header.message_length as usize - 16;
    let mut body = vec![0u8; body_len];
    stream.read_exact(&mut body).await.unwrap();
    (header, body)
}

/// Extracts the body document from an OP_MSG response (flags + kind0 + doc).
fn op_msg_body(body: &[u8]) -> Document {
    let doc_bytes = &body[5..];
    Document::from_reader(doc_bytes).unwrap()
}

async fn start_server() -> std::net::SocketAddr {
    let server = MongoDbServer::bind(0).await.unwrap();
    let addr = server.local_addr();
    tokio::spawn(async move {
        let _ = server.run().await;
    });
    addr
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn hello_handshake_over_op_msg() {
    let addr = start_server().await;
    let mut stream = TcpStream::connect(addr).await.unwrap();

    let cmd = doc! { "hello": 1, "$db": "admin" };
    let request = wire::encode_op_msg(0, &cmd);
    stream.write_all(&request).await.unwrap();
    stream.flush().await.unwrap();

    let (header, body) = read_message(&mut stream).await;
    assert_eq!(header.op_code, wire::OP_MSG);
    let reply = op_msg_body(&body);
    assert!(reply.get_bool("isWritablePrimary").unwrap());
    assert_eq!(reply.get_f64("ok").unwrap(), 1.0);
    assert_eq!(
        reply.get_i32("maxMessageSizeBytes").unwrap(),
        cosmos_mongodb::commands::MAX_MESSAGE_SIZE_BYTES
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn ping_and_build_info() {
    let addr = start_server().await;
    let mut stream = TcpStream::connect(addr).await.unwrap();

    stream
        .write_all(&wire::encode_op_msg(0, &doc! { "ping": 1, "$db": "admin" }))
        .await
        .unwrap();
    let (_, body) = read_message(&mut stream).await;
    assert_eq!(op_msg_body(&body).get_f64("ok").unwrap(), 1.0);

    stream
        .write_all(&wire::encode_op_msg(
            0,
            &doc! { "buildInfo": 1, "$db": "admin" },
        ))
        .await
        .unwrap();
    let (_, body) = read_message(&mut stream).await;
    let reply = op_msg_body(&body);
    assert_eq!(
        reply.get_str("version").unwrap(),
        cosmos_mongodb::commands::SERVER_VERSION
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn legacy_op_query_handshake() {
    let addr = start_server().await;
    let mut stream = TcpStream::connect(addr).await.unwrap();

    let mut body = Vec::new();
    body.extend_from_slice(&0i32.to_le_bytes()); // flags
    body.extend_from_slice(b"admin.$cmd\0");
    body.extend_from_slice(&0i32.to_le_bytes()); // numberToSkip
    body.extend_from_slice(&(-1i32).to_le_bytes()); // numberToReturn
    let mut q = Vec::new();
    doc! { "isMaster": 1 }.to_writer(&mut q).unwrap();
    body.extend_from_slice(&q);

    let mut msg = Vec::new();
    let total = (16 + body.len()) as i32;
    msg.extend_from_slice(&total.to_le_bytes());
    msg.extend_from_slice(&1i32.to_le_bytes()); // requestId
    msg.extend_from_slice(&0i32.to_le_bytes()); // responseTo
    msg.extend_from_slice(&wire::OP_QUERY.to_le_bytes());
    msg.extend_from_slice(&body);

    stream.write_all(&msg).await.unwrap();
    stream.flush().await.unwrap();

    let (header, body) = read_message(&mut stream).await;
    assert_eq!(header.op_code, wire::OP_REPLY);
    let reply = Document::from_reader(&body[20..]).unwrap();
    assert!(reply.get_bool("ismaster").unwrap());
}
