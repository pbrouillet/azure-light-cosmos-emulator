//! Integration tests for the NoSQL REST API, driving the router with an
//! in-memory store via `tower`'s `oneshot`.

use std::sync::Arc;

use axum::body::{to_bytes, Body};
use axum::http::{HeaderMap, Request, StatusCode};
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
    let (status, _, value) = send_with_headers(app, method, uri, headers, body).await;
    (status, value)
}

async fn send_with_headers(
    app: &Router,
    method: &str,
    uri: &str,
    headers: &[(&str, &str)],
    body: Option<Value>,
) -> (StatusCode, HeaderMap, Value) {
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
    let response_headers = response.headers().clone();
    let bytes = to_bytes(response.into_body(), usize::MAX).await.unwrap();
    let value = if bytes.is_empty() {
        Value::Null
    } else {
        serde_json::from_slice(&bytes).unwrap_or(Value::Null)
    };
    (status, response_headers, value)
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
async fn programmability_rest_crud_and_sproc_execute() {
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

    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/sprocs",
        &[],
        Some(json!({
            "id": "add",
            "body": "function(a, b) { getContext().getResponse().setBody(a + b); }"
        })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["id"], "add");
    assert_eq!(body["_self"], "dbs/db1/colls/c1/sprocs/add/");

    let (status, body) = send(&app, "GET", "/dbs/db1/colls/c1/sprocs", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["_count"], 1);
    assert_eq!(body["StoredProcedures"][0]["id"], "add");

    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/sprocs/add",
        &[("x-ms-documentdb-partitionkey", "[\"p1\"]")],
        Some(json!([2, 40])),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body, json!(42));

    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/triggers",
        &[],
        Some(json!({
            "id": "stamp",
            "body": "function(){}",
            "triggerType": "Pre",
            "triggerOperation": "Create"
        })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["triggerType"], "Pre");
    assert_eq!(body["triggerOperation"], "Create");

    let (status, body) = send(&app, "GET", "/dbs/db1/colls/c1/triggers", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["_count"], 1);

    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/udfs",
        &[],
        Some(json!({ "id": "tax", "body": "function(v) { return v * 1.1; }" })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["_self"], "dbs/db1/colls/c1/udfs/tax/");

    let (status, body) = send(
        &app,
        "PUT",
        "/dbs/db1/colls/c1/udfs/tax",
        &[],
        Some(json!({ "body": "function(v) { return v; }" })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["id"], "tax");

    let (status, _) = send(&app, "DELETE", "/dbs/db1/colls/c1/sprocs/add", &[], None).await;
    assert_eq!(status, StatusCode::NO_CONTENT);
}

#[tokio::test]
async fn document_writes_honor_pre_trigger_headers() {
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
        "/dbs/db1/colls/c1/triggers",
        &[],
        Some(json!({
            "id": "createStamp",
            "triggerType": "Pre",
            "triggerOperation": "Create",
            "body": "function(){ var r = getContext().getRequest(); var d = r.getBody(); d.createdByTrigger = true; r.setBody(d); }"
        })),
    )
    .await;

    let (status, body) = send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/docs",
        &[("x-ms-documentdb-pre-trigger-include", "createStamp")],
        Some(json!({ "id": "d1", "pk": "p1" })),
    )
    .await;
    assert_eq!(status, StatusCode::CREATED);
    assert_eq!(body["createdByTrigger"], true);

    send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/triggers",
        &[],
        Some(json!({
            "id": "replaceStamp",
            "triggerType": "Pre",
            "triggerOperation": "Replace",
            "body": "function(){ var r = getContext().getRequest(); var d = r.getBody(); d.value = 99; r.setBody(d); }"
        })),
    )
    .await;
    let (status, body) = send(
        &app,
        "PUT",
        "/dbs/db1/colls/c1/docs/d1",
        &[("x-ms-documentdb-pre-trigger-include", "replaceStamp")],
        Some(json!({ "id": "d1", "pk": "p1", "value": 1 })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["value"], 99);

    send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/triggers",
        &[],
        Some(json!({
            "id": "wrongDelete",
            "triggerType": "Post",
            "triggerOperation": "Delete",
            "body": "function(){}"
        })),
    )
    .await;
    let (status, _) = send(
        &app,
        "DELETE",
        "/dbs/db1/colls/c1/docs/d1",
        &[
            ("x-ms-documentdb-partitionkey", "[\"p1\"]"),
            ("x-ms-documentdb-pre-trigger-include", "wrongDelete"),
        ],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::BAD_REQUEST);

    send(
        &app,
        "POST",
        "/dbs/db1/colls/c1/triggers",
        &[],
        Some(json!({
            "id": "deleteCheck",
            "triggerType": "Pre",
            "triggerOperation": "Delete",
            "body": "function(){ if (getContext().getRequest().getBody().id !== 'd1') { throw new Error('missing document'); } }"
        })),
    )
    .await;
    let (status, _) = send(
        &app,
        "DELETE",
        "/dbs/db1/colls/c1/docs/d1",
        &[
            ("x-ms-documentdb-partitionkey", "[\"p1\"]"),
            ("x-ms-documentdb-pre-trigger-include", "deleteCheck"),
        ],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::NO_CONTENT);
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
async fn addresses_returns_single_partition_address() {
    let state =
        AppState::new(Arc::new(InMemoryDocumentStore::new())).with_address_endpoint(9090, true);
    let app = router(state);

    let (status, headers, body) = send_with_headers(&app, "GET", "/addresses", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(headers["x-ms-request-charge"], "1.00");
    assert_eq!(headers["x-ms-serviceversion"], "2024-11-30");
    assert_eq!(body["_count"], 1);

    let address = &body["Addresses"][0];
    assert_eq!(address["id"], "0");
    assert_eq!(address["partitionKeyRangeId"], "0");
    assert_eq!(address["protocol"], "https");
    assert_eq!(address["logicalUri"], "rntbd://localhost:9090/");
    assert_eq!(address["physicalUri"], "https://localhost:9090/");
    assert_eq!(address["isPrimary"], true);
}

#[tokio::test]
async fn attachments_return_gone_for_collection_and_item_routes() {
    let app = app();
    let cases = [
        ("GET", "/dbs/db1/colls/c1/docs/doc1/attachments"),
        ("POST", "/dbs/db1/colls/c1/docs/doc1/attachments"),
        ("GET", "/dbs/db1/colls/c1/docs/doc1/attachments/attachment1"),
        ("PUT", "/dbs/db1/colls/c1/docs/doc1/attachments/attachment1"),
        (
            "DELETE",
            "/dbs/db1/colls/c1/docs/doc1/attachments/attachment1",
        ),
    ];

    for (method, uri) in cases {
        let (status, body) = send(&app, method, uri, &[], None).await;
        assert_eq!(status, StatusCode::GONE, "{method} {uri}");
        assert_eq!(body["code"], "Gone");
        assert!(body["message"]
            .as_str()
            .unwrap()
            .contains("Attachments are deprecated in Azure Cosmos DB"));
    }
}

#[tokio::test]
async fn consistency_rejects_stronger_override() {
    let app = app();
    let (status, body) = send(
        &app,
        "GET",
        "/dbs",
        &[("x-ms-consistency-level", "Strong")],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::BAD_REQUEST);
    assert_eq!(body["code"], "BadRequest");
    assert!(body["message"]
        .as_str()
        .unwrap()
        .contains("stronger than the account default 'Session'"));
}

#[tokio::test]
async fn consistency_allows_ahead_session_token_like_dotnet() {
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
        "GET",
        "/dbs/db1/colls/c1/docs/d1",
        &[
            ("x-ms-documentdb-partitionkey", "[\"a\"]"),
            ("x-ms-session-token", "0:999"),
        ],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["id"], "d1");
}

#[tokio::test]
async fn emulator_info_stats_and_settings_routes() {
    let state =
        AppState::new(Arc::new(InMemoryDocumentStore::new())).with_address_endpoint(8181, false);
    let app = router(state);
    send(&app, "POST", "/dbs", &[], Some(json!({ "id": "db1" }))).await;
    send(
        &app,
        "POST",
        "/dbs/db1/colls",
        &[],
        Some(json!({ "id": "c1", "partitionKey": { "paths": ["/pk"] } })),
    )
    .await;

    let (status, body) = send(&app, "GET", "/api/emulator/info", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["name"], "Azure Cosmos DB Light Emulator");
    assert_eq!(body["endpoints"]["noSql"], "http://localhost:8181");
    assert_eq!(body["configuration"]["port"], 8181);
    assert_eq!(body["configuration"]["enableEntraId"], false);

    let (status, body) = send(&app, "GET", "/api/emulator/stats", &[], None).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["databaseCount"], 1);
    assert_eq!(body["containerCount"], 1);

    let (status, body) = send(
        &app,
        "PUT",
        "/api/emulator/settings",
        &[],
        Some(json!({ "enableEntraId": true, "tenantId": "tenant", "clientId": "client" })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["configuration"]["enableEntraId"], true);
    assert_eq!(body["configuration"]["tenantId"], "tenant");
    assert_eq!(body["configuration"]["clientId"], "client");
}

#[tokio::test]
async fn emulator_throughput_routes_get_and_update() {
    let app = app();
    send(
        &app,
        "POST",
        "/dbs",
        &[],
        Some(json!({ "id": "db1", "maxThroughput": 1200 })),
    )
    .await;
    send(
        &app,
        "POST",
        "/dbs/db1/colls",
        &[],
        Some(json!({ "id": "c1", "partitionKey": { "paths": ["/pk"] }, "maxThroughput": 800 })),
    )
    .await;

    let (status, body) = send(
        &app,
        "GET",
        "/api/emulator/throughput/database/db1",
        &[],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["id"], "db1");
    assert_eq!(body["maxThroughput"], Value::Null);

    let (status, body) = send(
        &app,
        "PUT",
        "/api/emulator/throughput/database/db1",
        &[],
        Some(json!({ "maxThroughput": 2400 })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["maxThroughput"], 2400);

    let (status, body) = send(
        &app,
        "GET",
        "/api/emulator/throughput/database/db1",
        &[],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["maxThroughput"], 2400);

    let (status, body) = send(
        &app,
        "GET",
        "/api/emulator/throughput/container/db1/c1",
        &[],
        None,
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["id"], "c1");
    assert_eq!(body["databaseId"], "db1");
    assert_eq!(body["maxThroughput"], 800);

    let (status, body) = send(
        &app,
        "PUT",
        "/api/emulator/throughput/container/db1/c1",
        &[],
        Some(json!({ "maxThroughput": 1600 })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["maxThroughput"], 1600);
}

#[tokio::test]
async fn emulator_explain_returns_dotnet_shaped_payload() {
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

    let (status, body) = send(
        &app,
        "POST",
        "/api/emulator/explain",
        &[],
        Some(json!({
            "databaseId": "db1",
            "containerId": "c1",
            "query": "SELECT * FROM c WHERE c.pk = 'a'"
        })),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(body["query"], "SELECT * FROM c WHERE c.pk = 'a'");
    assert!(body["queryPlan"].is_object());
    assert!(body["estimatedRuCharge"]["total"].as_f64().unwrap() > 0.0);
    assert!(body["indexAnalysis"]["indexingPolicyPaths"]["included"].is_array());
    assert!(body["warnings"].is_array());
    assert!(body["educationalNotes"].is_array());
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
