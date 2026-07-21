//! Integration tests for the NoSQL REST API, driving the router with an
//! in-memory store via `tower`'s `oneshot`.

use std::sync::Arc;

use axum::body::{to_bytes, Body};
use axum::http::{Request, StatusCode};
use axum::Router;
use cosmos_auth::MasterKeyAuthProvider;
use cosmos_nosql::{router, AppState};
use cosmos_storage::{InMemoryChangeFeedProvider, InMemoryChangeLog, InMemoryDocumentStore};
use serde_json::{json, Value};
use tower::ServiceExt;

fn app() -> Router {
    router(AppState::new(Arc::new(InMemoryDocumentStore::new())))
}

async fn send(
    app: &Router,
    method: &str,
    uri: &str,
    headers: &[(&str, &str)],
    body: Option<Value>,
) -> (StatusCode, Value) {
    let mut builder = Request::builder().method(method).uri(uri);
    for (name, value) in headers {
        builder = builder.header(*name, *value);
    }
    let body = match body {
        Some(v) => Body::from(serde_json::to_vec(&v).unwrap()),
        None => Body::empty(),
    };
    let response = app
        .clone()
        .oneshot(builder.body(body).unwrap())
        .await
        .unwrap();
    let status = response.status();
    let bytes = to_bytes(response.into_body(), usize::MAX).await.unwrap();
    let value = if bytes.is_empty() {
        Value::Null
    } else {
        serde_json::from_slice(&bytes).unwrap_or(Value::Null)
    };
    (status, value)
}

#[tokio::test]
async fn database_crud_roundtrip() {
    let app = app();
    let (status, body) = send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["id"], "db1");
    assert!(body["_rid"].as_str().is_some());
    assert!(body["_self"].as_str().unwrap().starts_with("dbs/"));

    let (status, body) = send(&app, "GET", "/dbs", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["_count"], 1);

    let (status, _) = send(&app, "GET", "/dbs/db1", &[], None).await;
    assert_eq!(status, StatusCode::OK);

    let (status, _) = send(&app, "DELETE", "/dbs/db1", &[], None).await;
    assert_eq!(status, StatusCode::NO_CONTENT);

    let (status, _) = send(&app, "GET", "/dbs/db1", &[], None).await;
    assert_eq!(status, StatusCode::NOT_FOUND);
}

#[tokio::test]
async fn missing_id_is_bad_request() {
    let app = app();
    let (status, body) = send(&app, "POST", "/dbs", &[], Some(json!({}))).await;
    assert_eq!(status, StatusCode::BAD_REQUEST);
    assert_eq!(body["code"], "BadRequest");
}

#[tokio::test]
async fn container_and_document_crud() {
    let app = app();
    send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls",
        &[],
        Some(json!({ "id": "c1", "partitionKey": { "paths": ["/pk"], "kind": "Hash" } })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["partitionKey"]["paths"][0], "/pk");
    assert_eq!(body["partitionKey"]["kind"], "Hash");
    assert_eq!(body["indexingPolicy"]["IndexingMode"], 0);

    // Create a document.
    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/docs",
        &[],
        Some(json!({ "id": "doc1", "pk": "tenant-a", "value": 42 })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["id"], "doc1");
    assert!(body["_etag"].as_str().is_some());

    // Read it back with the correct partition key.
    let (status, body) = send(
        &app,
        "GET",
        "/dbs/db1/colls/c1/docs/doc1",
        &[("x-ms-documentdb-partitionkey", "[\"tenant-a\"]")],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["value"], 42);

    // Reading without a partition key header is a 400.
    let (status, _) = send(&app, "GET", "/dbs/db1/colls/c1/docs/doc1", &[], None).await;
    assert_eq!(status, StatusCode::BAD_REQUEST);

    // Reading with the wrong partition key is a 404.
    let (status, _) = send(
        &app,
        "GET",
        "/dbs/db1/colls/c1/docs/doc1",
        &[("x-ms-documentdb-partitionkey", "[\"tenant-b\"]")],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::NOT_FOUND);

    // Patch it.
    let (status, body) = send(
        &app,
        "PATCH",
        "/dbs/db1/colls/c1/docs/doc1",
        &[("x-ms-documentdb-partitionkey", "[\"tenant-a\"]")],
        Some(json!({ "operations": [{ "op": "set", "path": "/value", "value": 99 }] })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["value"], 99);

    // Delete it.
    let (status, _) = send(
        &app,
        "DELETE",
        "/dbs/db1/colls/c1/docs/doc1",
        &[("x-ms-documentdb-partitionkey", "[\"tenant-a\"]")],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::NO_CONTENT);
}

#[tokio::test]
async fn query_fallback_returns_all_documents() {
    let app = app();
    send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    send(
        &app,
        "POST",
        "/dbs/db1/colls",
        &[],
        Some(json!({ "id": "c1", "partitionKey": { "paths": ["/pk"] } })),
    )
    .await;
    send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/docs",
        &[],
        Some(json!({ "id": "d1", "pk": "a" })),
    )
    .await;

    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/docs",
        &[("x-ms-documentdb-isquery", "true")],
        Some(json!({ "query": "SELECT * FROM c" })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["_count"], 1);
    assert_eq!(body["Documents"][0]["id"], "d1");
}

#[tokio::test]
async fn upsert_updates_existing() {
    let app = app();
    send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    send(
        &app,
        "POST",
        "/dbs/db1/colls",
        &[],
        Some(json!({ "id": "c1", "partitionKey": { "paths": ["/pk"] } })),
    )
    .await;
    let headers = [("x-ms-documentdb-is-upsert", "true")];
    let (status, _) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/docs",
        &headers,
        Some(json!({ "id": "d1", "pk": "a", "n": 1 })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/docs",
        &headers,
        Some(json!({ "id": "d1", "pk": "a", "n": 2 })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["n"], 2);
}

#[tokio::test]
async fn users_and_permissions() {
    let app = app();
    send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    let (status, _) = send(
        &app,
        "POST",
        "/dbs/db1/users",
        &[],
        Some(json!({ "id": "user1" })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);

    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/users/user1/permissions",
        &[],
        Some(json!({ "id": "perm1", "permissionMode": "Read", "resource": "dbs/db1/colls/c1" })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["permissionMode"], "Read");
    assert_eq!(body["resource"], "dbs/db1/colls/c1");

    let (status, body) = send(&app, "GET", "/dbs/db1/users/user1/permissions", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["_count"], 1);
}

#[tokio::test]
async fn offers_list_is_wired() {
    let app = app();
    let (status, body) = send(&app, "GET", "/offers", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert!(body["Offers"].is_array());
}

#[tokio::test]
async fn pkranges_honours_if_none_match() {
    let app = app();
    let (status, body) = send(&app, "GET", "/dbs/db1/colls/c1/pkranges", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["_count"], 1);
    assert_eq!(body["PartitionKeyRanges"][0]["id"], "0");

    let (status, _) = send(
        &app,
        "GET",
        "/dbs/db1/colls/c1/pkranges",
        &[("If-None-Match", "\"00000000-0000-0000-0000-000000000000\"")],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::NOT_MODIFIED);
}

#[tokio::test]
async fn changefeed_requires_aim_header_and_reports_empty() {
    let store = Arc::new(InMemoryDocumentStore::new());
    let change_feed = Arc::new(InMemoryChangeFeedProvider::new(Arc::new(
        InMemoryChangeLog::default(),
    )));
    let state = AppState::new(store).with_change_feed(change_feed);
    let app = router(state);

    // Missing A-IM header → 400.
    let (status, _) = send(&app, "GET", "/dbs/db1/colls/c1/docs/changefeed", &[], None).await;
    assert_eq!(status, StatusCode::BAD_REQUEST);

    // With A-IM header but no changes → 304.
    let (status, _) = send(
        &app,
        "GET",
        "/dbs/db1/colls/c1/docs/changefeed",
        &[("A-IM", "Incremental feed")],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::NOT_MODIFIED);
}

#[tokio::test]
async fn batch_executes_operations() {
    let app = app();
    send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    send(
        &app,
        "POST",
        "/dbs/db1/colls",
        &[],
        Some(json!({ "id": "c1", "partitionKey": { "paths": ["/pk"] } })),
    )
    .await;

    let ops = json!([
        { "operationType": "Create", "resourceBody": { "id": "b1", "pk": "a" } },
        { "operationType": "Read", "id": "b1" }
    ]);
    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1",
        &[
            ("x-ms-cosmos-is-batch-request", "true"),
            ("x-ms-documentdb-partitionkey", "[\"a\"]"),
        ],
        Some(ops),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert!(body.is_array());
    assert_eq!(body.as_array().unwrap().len(), 2);
}

#[tokio::test]
async fn batch_without_header_is_not_found() {
    let app = app();
    send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    send(
        &app,
        "POST",
        "/dbs/db1/colls",
        &[],
        Some(json!({ "id": "c1", "partitionKey": { "paths": ["/pk"] } })),
    )
    .await;
    let (status, _) = send(&app, "POST", "/dbs/db1/colls/c1", &[], Some(json!([]))).await;
    assert_eq!(status, StatusCode::NOT_FOUND);
}

#[tokio::test]
async fn auth_rejects_missing_header_but_allows_valid_signature() {
    let store = Arc::new(InMemoryDocumentStore::new());
    let provider = Arc::new(MasterKeyAuthProvider::default());
    let state = AppState::new(store).with_auth(provider.clone());
    let app = router(state);

    // No Authorization header → 401.
    let (status, _) = send(&app, "GET", "/dbs", &[], None).await;
    assert_eq!(status, StatusCode::UNAUTHORIZED);

    // pkranges is on the skip list → not rejected.
    let (status, _) = send(&app, "GET", "/dbs/db1/colls/c1/pkranges", &[], None).await;
    assert_eq!(status, StatusCode::OK);

    // Explorer header bypasses auth.
    let (status, _) = send(&app, "GET", "/dbs", &[("x-ms-cosmos-explorer", "1")], None).await;
    assert_eq!(status, StatusCode::OK);

    // A valid master-key signature is accepted.
    let auth_header = provider.generate_auth_header("get", "dbs", "", "").unwrap();
    let (status, _) = send(
        &app,
        "GET",
        "/dbs",
        &[("Authorization", &auth_header)],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::OK);
}
