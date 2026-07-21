//! HTTP client helpers for `export` / `import`, issuing master-key–signed
//! requests against a running emulator (ports the .NET CLI's
//! `SendAuthenticatedAsync` + export/import flows).

use anyhow::{bail, Context, Result};
use cosmos_auth::MasterKeyAuthProvider;
use cosmos_core::models::headers as h;
use reqwest::{Client, Method, Response};
use serde_json::{json, Map, Value};

use crate::state::EmulatorInstanceState;

/// Builds an RFC 1123 date string (matching .NET `DateTimeOffset.ToString("r")`).
fn http_date() -> String {
    chrono::Utc::now()
        .format("%a, %d %b %Y %H:%M:%S GMT")
        .to_string()
}

async fn send(
    state: &EmulatorInstanceState,
    method: Method,
    path: &str,
    resource_type: &str,
    resource_link: &str,
    body: Option<&Value>,
    extra_headers: &[(&str, &str)],
) -> Result<Response> {
    let client = Client::new();
    let url = format!("{}/{}", state.endpoint().trim_end_matches('/'), path);
    let date = http_date();
    let auth = MasterKeyAuthProvider::new(state.master_key.clone());
    let auth_header = auth
        .generate_auth_header(method.as_str(), resource_type, resource_link, &date)
        .map_err(|e| anyhow::anyhow!("failed to sign request: {e}"))?;

    let mut req = client
        .request(method, &url)
        .header(h::AUTHORIZATION, auth_header)
        .header("x-ms-date", date)
        .header(reqwest::header::ACCEPT, "application/json");

    for (k, v) in extra_headers {
        req = req.header(*k, *v);
    }
    if let Some(body) = body {
        req = req
            .header(h::CONTENT_TYPE, "application/json")
            .body(serde_json::to_vec(body)?);
    }

    req.send().await.context("request failed")
}

async fn ensure_success(response: Response) -> Result<Value> {
    let status = response.status();
    let text = response.text().await.unwrap_or_default();
    if !status.is_success() {
        bail!("Request failed with status {status}. {text}");
    }
    if text.trim().is_empty() {
        return Ok(Value::Null);
    }
    Ok(serde_json::from_str(&text).unwrap_or(Value::Null))
}

/// Exports all databases, containers, and documents to a JSON document.
pub async fn export(state: &EmulatorInstanceState) -> Result<Value> {
    let mut databases_out = Vec::new();

    let dbs_resp = send(state, Method::GET, "dbs", "dbs", "", None, &[]).await?;
    let dbs_payload = ensure_success(dbs_resp).await?;
    let databases = dbs_payload
        .get("Databases")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();

    for database in databases {
        let Some(db_id) = database.get("id").and_then(Value::as_str) else {
            continue;
        };
        let mut containers_out = Vec::new();

        let colls_path = format!("dbs/{db_id}/colls");
        let colls_resp = send(
            state,
            Method::GET,
            &colls_path,
            "colls",
            &format!("dbs/{db_id}"),
            None,
            &[],
        )
        .await?;
        let colls_payload = ensure_success(colls_resp).await?;
        let containers = colls_payload
            .get("DocumentCollections")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();

        for container in containers {
            let Some(container_id) = container.get("id").and_then(Value::as_str) else {
                continue;
            };
            let mut documents_out = Vec::new();

            let query_body = json!({ "query": "SELECT * FROM c", "parameters": [] });
            let docs_path = format!("dbs/{db_id}/colls/{container_id}/docs");
            let docs_resp = send(
                state,
                Method::POST,
                &docs_path,
                "docs",
                &format!("dbs/{db_id}/colls/{container_id}"),
                Some(&query_body),
                &[(h::IS_QUERY, "true"), (h::ENABLE_CROSS_PARTITION, "true")],
            )
            .await?;
            let docs_payload = ensure_success(docs_resp).await?;
            if let Some(docs) = docs_payload.get("Documents").and_then(Value::as_array) {
                documents_out.extend(docs.iter().cloned());
            }

            let mut container_export = Map::new();
            container_export.insert("id".into(), json!(container_id));
            if let Some(pk) = container.get("partitionKey") {
                container_export.insert("partitionKey".into(), pk.clone());
            }
            if let Some(ip) = container.get("indexingPolicy") {
                container_export.insert("indexingPolicy".into(), ip.clone());
            }
            if let Some(ttl) = container.get("defaultTtl") {
                container_export.insert("defaultTtl".into(), ttl.clone());
            }
            container_export.insert("documents".into(), Value::Array(documents_out));
            containers_out.push(Value::Object(container_export));
        }

        databases_out.push(json!({ "id": db_id, "containers": containers_out }));
    }

    Ok(json!({ "databases": databases_out }))
}

/// Imports databases, containers, and documents from an exported JSON document.
pub async fn import(state: &EmulatorInstanceState, document: &Value) -> Result<()> {
    let databases = document
        .get("databases")
        .and_then(Value::as_array)
        .context("Import file is missing a 'databases' array.")?;

    for database in databases {
        let Some(db_id) = database.get("id").and_then(Value::as_str) else {
            continue;
        };

        create_if_missing(state, "dbs", "", &json!({ "id": db_id }), "dbs").await?;

        let containers = database
            .get("containers")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        for container in containers {
            let Some(container_id) = container.get("id").and_then(Value::as_str) else {
                continue;
            };

            let partition_key = container
                .get("partitionKey")
                .cloned()
                .unwrap_or_else(|| json!({ "paths": ["/id"], "kind": "Hash", "version": 2 }));
            let mut container_body = Map::new();
            container_body.insert("id".into(), json!(container_id));
            container_body.insert("partitionKey".into(), partition_key);
            if let Some(ip) = container.get("indexingPolicy") {
                container_body.insert("indexingPolicy".into(), ip.clone());
            }
            if let Some(ttl) = container.get("defaultTtl") {
                container_body.insert("defaultTtl".into(), ttl.clone());
            }

            create_if_missing(
                state,
                "colls",
                &format!("dbs/{db_id}"),
                &Value::Object(container_body),
                &format!("dbs/{db_id}/colls"),
            )
            .await?;

            let documents = container
                .get("documents")
                .and_then(Value::as_array)
                .cloned()
                .unwrap_or_default();
            for document in documents {
                let Value::Object(mut body) = document else {
                    continue;
                };
                for sys in ["_rid", "_self", "_etag", "_ts", "_attachments"] {
                    body.remove(sys);
                }
                let docs_path = format!("dbs/{db_id}/colls/{container_id}/docs");
                let resp = send(
                    state,
                    Method::POST,
                    &docs_path,
                    "docs",
                    &format!("dbs/{db_id}/colls/{container_id}"),
                    Some(&Value::Object(body)),
                    &[(h::IS_UPSERT, "true")],
                )
                .await?;
                ensure_success(resp).await?;
            }
        }
    }
    Ok(())
}

async fn create_if_missing(
    state: &EmulatorInstanceState,
    resource_type: &str,
    resource_link: &str,
    body: &Value,
    path: &str,
) -> Result<()> {
    let resp = send(
        state,
        Method::POST,
        path,
        resource_type,
        resource_link,
        Some(body),
        &[],
    )
    .await?;
    if resp.status() == reqwest::StatusCode::CONFLICT {
        return Ok(());
    }
    ensure_success(resp).await?;
    Ok(())
}

/// Checks whether the emulator's `/health` endpoint responds successfully.
pub async fn is_endpoint_healthy(state: &EmulatorInstanceState) -> bool {
    let url = format!("{}/health", state.endpoint().trim_end_matches('/'));
    match Client::new().get(&url).send().await {
        Ok(resp) => resp.status().is_success(),
        Err(_) => false,
    }
}
