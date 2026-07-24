//! TLS end-to-end smoke test.
//!
//! Boots the **real** [`cosmos_host`] router behind `axum_server`'s rustls
//! listener — the exact stack the production `--enable-ssl` path uses — with a
//! self-signed certificate generated the same way the host generates its
//! development cert (`rcgen`). It then drives it with a master-key–signed
//! request over `https://` using a `reqwest` rustls client, proving that an
//! external client can complete a TLS handshake and round-trip the Cosmos wire
//! contract over TLS. Unlike the SDK layer under `sdk/`, this runs as part of
//! `cargo test` (no network required).

use std::net::{SocketAddr, TcpListener};

use axum_server::tls_rustls::RustlsConfig;
use cosmos_auth::MasterKeyAuthProvider;
use cosmos_core::{ConsistencyLevel, StorageType};
use cosmos_host::{build_router, build_store, HostOptions};
use cosmos_parity::extract_resource_info;
use reqwest::{Method, StatusCode};
use serde_json::json;

fn http_date() -> String {
    chrono::Utc::now()
        .format("%a, %d %b %Y %H:%M:%S GMT")
        .to_string()
}

#[tokio::test]
async fn tls_signed_crud_round_trips_over_https() {
    // Install the aws-lc-rs crypto provider (idempotent); the test's dependency
    // closure contains both ring and aws-lc-rs, so rustls needs an explicit pick.
    let _ = rustls::crypto::aws_lc_rs::default_provider().install_default();

    let master_key = MasterKeyAuthProvider::generate_master_key();
    let opts = HostOptions {
        port: 0,
        mongo_port: None,
        enable_ssl: true,
        storage: StorageType::InMemory,
        data_dir: None,
        explorer_dir: None,
        master_key: Some(master_key.clone()),
        enable_entra: false,
        enable_throughput_enforcement: false,
        enable_maintenance: false,
        enable_request_tracking: false,
        consistency: ConsistencyLevel::Session,
    };
    let store = build_store(&opts).await.unwrap();
    let app = build_router(&opts, store);

    // Self-signed cert for localhost, mirroring the host's dev-cert generation.
    let certified = rcgen::generate_simple_self_signed(vec!["localhost".to_string()]).unwrap();
    let cert_pem = certified.cert.pem().into_bytes();
    let key_pem = certified.key_pair.serialize_pem().into_bytes();
    let config = RustlsConfig::from_pem(cert_pem, key_pem).await.unwrap();

    // Bind an ephemeral port ourselves so we know where to point the client.
    let listener = TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], 0))).unwrap();
    let addr = listener.local_addr().unwrap();

    let server = tokio::spawn(async move {
        let _ = axum_server::from_tcp_rustls(listener, config)
            .serve(app.into_make_service())
            .await;
    });

    // A rustls client that accepts the self-signed cert.
    let client = reqwest::Client::builder()
        .danger_accept_invalid_certs(true)
        .build()
        .unwrap();
    let base = format!("https://{addr}");
    let auth = MasterKeyAuthProvider::new(master_key);

    let sign = |method: &Method, path: &str| {
        let (rt, rl) = extract_resource_info(path);
        let date = http_date();
        let header = auth
            .generate_auth_header(method.as_str(), &rt, &rl, &date)
            .unwrap();
        (header, date)
    };

    // POST /dbs (create) over TLS.
    let db = format!(
        "tlsdb-{}",
        chrono::Utc::now().timestamp_nanos_opt().unwrap()
    );
    let (header, date) = sign(&Method::POST, "/dbs");
    let create = client
        .post(format!("{base}/dbs"))
        .header("authorization", header)
        .header("x-ms-date", date)
        .header("x-ms-version", "2018-12-31")
        .header(reqwest::header::CONTENT_TYPE, "application/json")
        .body(serde_json::to_vec(&json!({ "id": db })).unwrap())
        .send()
        .await
        .expect("POST /dbs over https failed");
    assert_eq!(create.status(), StatusCode::CREATED);

    // GET /dbs/{db} (read back) over TLS.
    let path = format!("/dbs/{db}");
    let (header, date) = sign(&Method::GET, &path);
    let read = client
        .get(format!("{base}{path}"))
        .header("authorization", header)
        .header("x-ms-date", date)
        .header("x-ms-version", "2018-12-31")
        .send()
        .await
        .expect("GET /dbs/{db} over https failed");
    assert_eq!(read.status(), StatusCode::OK);
    let body: serde_json::Value = read.json().await.unwrap();
    assert_eq!(body["id"].as_str(), Some(db.as_str()));

    server.abort();
}
