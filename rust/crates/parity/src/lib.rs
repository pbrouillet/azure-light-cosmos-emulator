//! Black-box parity harness for the Rust Cosmos emulator.
//!
//! Ports the intent of the .NET `tests/Parity.Tests` suite: it boots the **real**
//! [`cosmos_host`] Axum application on an ephemeral TCP port and drives it with
//! master-key–signed HTTP requests over an actual socket (via `reqwest`), rather
//! than the in-process `tower::oneshot` shortcut used by the per-crate unit
//! tests. This validates the end-to-end wire contract — HMAC `Authorization`
//! signing, `x-ms-*` headers, HTTP status codes, and response body shapes —
//! exactly as an external Cosmos SDK client would exercise it.
//!
//! The harness derives `(resourceType, resourceLink)` from the request method and
//! path using the *same* algorithm the server's auth middleware uses
//! ([`extract_resource_info`]), so a caller only supplies `(method, path, body)`
//! and signatures always match what the server recomputes.
//!
//! An optional official-SDK layer (Node `@azure/cosmos`, Python `azure-cosmos`)
//! lives under `crates/parity/sdk/` and is documented in `PARITY.md`; it requires
//! network access and is intentionally **not** part of `cargo test`.

use std::net::SocketAddr;

use anyhow::Result;
use cosmos_auth::MasterKeyAuthProvider;
use cosmos_core::{ConsistencyLevel, StorageType};
use cosmos_host::{build_router, build_store, HostOptions};
use reqwest::{Client, Method, Response};
use serde_json::{json, Value};

/// A running emulator instance plus a signed HTTP client pointed at it.
///
/// The backing store is [`StorageType::InMemory`] so each harness is fully
/// isolated and needs no on-disk cleanup. The server task is aborted when the
/// harness is dropped.
pub struct ParityHarness {
    base_url: String,
    master_key: String,
    auth: MasterKeyAuthProvider,
    client: Client,
    server: tokio::task::JoinHandle<()>,
}

impl ParityHarness {
    /// Boots a fresh in-memory emulator on an ephemeral loopback port with a
    /// freshly generated master key and HMAC auth enforcement enabled.
    pub async fn start() -> Result<Self> {
        let master_key = MasterKeyAuthProvider::generate_master_key();
        let opts = HostOptions {
            port: 0,
            storage: StorageType::InMemory,
            data_dir: None,
            explorer_dir: None,
            master_key: Some(master_key.clone()),
            consistency: ConsistencyLevel::Session,
        };

        let store = build_store(&opts).await?;
        let app = build_router(&opts, store);

        let listener = tokio::net::TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], 0))).await?;
        let addr = listener.local_addr()?;
        let server = tokio::spawn(async move {
            let _ = axum::serve(listener, app).await;
        });

        Ok(Self {
            base_url: format!("http://{addr}"),
            auth: MasterKeyAuthProvider::new(master_key.clone()),
            master_key,
            client: Client::new(),
            server,
        })
    }

    /// The base URL the emulator is listening on (e.g. `http://127.0.0.1:54321`).
    pub fn base_url(&self) -> &str {
        &self.base_url
    }

    /// The generated master key for this instance.
    pub fn master_key(&self) -> &str {
        &self.master_key
    }

    /// Issues a master-key–signed request. `(resourceType, resourceLink)` are
    /// derived from `method`/`path` exactly as the server's auth middleware
    /// derives them, so the HMAC signature always matches.
    pub async fn send(
        &self,
        method: Method,
        path: &str,
        body: Option<Value>,
        extra_headers: &[(&str, &str)],
    ) -> Result<Response> {
        let (resource_type, resource_link) = extract_resource_info(path);
        let date = http_date();
        let auth_header = self
            .auth
            .generate_auth_header(method.as_str(), &resource_type, &resource_link, &date)
            .map_err(|e| anyhow::anyhow!("failed to sign request: {e}"))?;

        let url = format!("{}/{}", self.base_url, path.trim_start_matches('/'));
        let mut req = self
            .client
            .request(method, &url)
            .header("authorization", auth_header)
            .header("x-ms-date", date)
            .header("x-ms-version", "2018-12-31")
            .header(reqwest::header::ACCEPT, "application/json");

        for (k, v) in extra_headers {
            req = req.header(*k, *v);
        }
        if let Some(b) = body {
            req = req
                .header(reqwest::header::CONTENT_TYPE, "application/json")
                .body(serde_json::to_vec(&b)?);
        }

        Ok(req.send().await?)
    }

    /// Issues an **unsigned** request (no `Authorization` header) — used to
    /// assert that auth is actually enforced (expects `401`).
    pub async fn send_unsigned(&self, method: Method, path: &str) -> Result<Response> {
        let url = format!("{}/{}", self.base_url, path.trim_start_matches('/'));
        Ok(self
            .client
            .request(method, &url)
            .header(reqwest::header::ACCEPT, "application/json")
            .send()
            .await?)
    }

    /// Convenience: create a database `id` via a signed `POST /dbs`.
    pub async fn create_database(&self, id: &str) -> Result<Response> {
        self.send(Method::POST, "/dbs", Some(json!({ "id": id })), &[])
            .await
    }

    /// Convenience: create a container with a single-path hash partition key.
    pub async fn create_container(
        &self,
        db: &str,
        id: &str,
        partition_key_path: &str,
    ) -> Result<Response> {
        let body = json!({
            "id": id,
            "partitionKey": { "paths": [partition_key_path], "kind": "Hash" }
        });
        self.send(Method::POST, &format!("/dbs/{db}/colls"), Some(body), &[])
            .await
    }
}

impl Drop for ParityHarness {
    fn drop(&mut self) {
        self.server.abort();
    }
}

/// RFC 1123 date, matching .NET `DateTimeOffset.ToString("r")`.
fn http_date() -> String {
    chrono::Utc::now()
        .format("%a, %d %b %Y %H:%M:%S GMT")
        .to_string()
}

/// Derives `(resourceType, resourceLink)` from a request path, mirroring the
/// server-side `CosmosAuthMiddleware` extraction. `resourceType` is lowercased;
/// `resourceLink` preserves original casing (name-based links are case-sensitive
/// in the HMAC payload).
pub fn extract_resource_info(path: &str) -> (String, String) {
    let segments: Vec<&str> = path
        .trim_matches('/')
        .split('/')
        .filter(|s| !s.is_empty())
        .collect();

    if segments.is_empty() {
        return (String::new(), String::new());
    }

    let resource_type = match segments.len() {
        1 | 2 => segments[0],
        3 | 4 => segments[2],
        5 | 6 => segments[4],
        _ => segments[segments.len() - 1],
    };

    let resource_link = match segments.len() {
        1 => String::new(),
        2 | 3 => segments[..2].join("/"),
        4 | 5 => segments[..4].join("/"),
        6 => segments[..6].join("/"),
        _ => segments.join("/"),
    };

    (resource_type.to_ascii_lowercase(), resource_link)
}

#[cfg(test)]
mod tests {
    use super::extract_resource_info;

    #[test]
    fn resource_info_matches_server_extraction() {
        assert_eq!(extract_resource_info("/dbs"), ("dbs".into(), "".into()));
        assert_eq!(
            extract_resource_info("/dbs/mydb"),
            ("dbs".into(), "dbs/mydb".into())
        );
        assert_eq!(
            extract_resource_info("/dbs/mydb/colls"),
            ("colls".into(), "dbs/mydb".into())
        );
        assert_eq!(
            extract_resource_info("/dbs/mydb/colls/mycoll"),
            ("colls".into(), "dbs/mydb/colls/mycoll".into())
        );
        assert_eq!(
            extract_resource_info("/dbs/mydb/colls/mycoll/docs"),
            ("docs".into(), "dbs/mydb/colls/mycoll".into())
        );
        assert_eq!(
            extract_resource_info("/dbs/mydb/colls/mycoll/docs/doc1"),
            ("docs".into(), "dbs/mydb/colls/mycoll/docs/doc1".into())
        );
    }
}
