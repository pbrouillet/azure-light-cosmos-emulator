//! Black-box parity smoke tests. Port `tests/Parity.Tests/SmokeTests.cs`,
//! driving the real host over a socket with master-key–signed requests.

use cosmos_parity::ParityHarness;
use reqwest::{Method, StatusCode};
use serde_json::json;

fn unique(prefix: &str) -> String {
    let n = chrono::Utc::now().timestamp_nanos_opt().unwrap_or_default();
    format!("{prefix}-{n:x}")
}

#[tokio::test]
async fn create_database_should_succeed() {
    let h = ParityHarness::start().await.unwrap();
    let db = unique("db");

    let create = h.create_database(&db).await.unwrap();
    assert_eq!(create.status(), StatusCode::CREATED);

    let read = h
        .send(Method::GET, &format!("/dbs/{db}"), None, &[])
        .await
        .unwrap();
    assert_eq!(read.status(), StatusCode::OK);
    let body: serde_json::Value = read.json().await.unwrap();
    assert_eq!(body["id"].as_str(), Some(db.as_str()));
}

#[tokio::test]
async fn create_container_should_succeed() {
    let h = ParityHarness::start().await.unwrap();
    let db = unique("db");
    let coll = unique("coll");
    assert_eq!(
        h.create_database(&db).await.unwrap().status(),
        StatusCode::CREATED
    );

    let create = h
        .create_container(&db, &coll, "/partitionKey")
        .await
        .unwrap();
    assert_eq!(create.status(), StatusCode::CREATED);

    let read = h
        .send(Method::GET, &format!("/dbs/{db}/colls/{coll}"), None, &[])
        .await
        .unwrap();
    assert_eq!(read.status(), StatusCode::OK);
    let body: serde_json::Value = read.json().await.unwrap();
    assert_eq!(body["id"].as_str(), Some(coll.as_str()));
    let paths = body["partitionKey"]["paths"].as_array().unwrap();
    assert_eq!(paths[0].as_str(), Some("/partitionKey"));
}

#[tokio::test]
async fn crud_document_should_succeed() {
    let h = ParityHarness::start().await.unwrap();
    let db = unique("db");
    let coll = unique("coll");
    h.create_database(&db).await.unwrap();
    h.create_container(&db, &coll, "/partitionKey")
        .await
        .unwrap();

    let doc_id = unique("doc");
    let pk = "tenant-1";
    let coll_path = format!("/dbs/{db}/colls/{coll}");
    let doc_path = format!("{coll_path}/docs/{doc_id}");
    let pk_header: [(&str, &str); 1] = [("x-ms-documentdb-partitionkey", "[\"tenant-1\"]")];

    // Create
    let create = h
        .send(
            Method::POST,
            &format!("{coll_path}/docs"),
            Some(json!({ "id": doc_id, "partitionKey": pk, "value": "created" })),
            &[],
        )
        .await
        .unwrap();
    assert_eq!(create.status(), StatusCode::CREATED);
    let created: serde_json::Value = create.json().await.unwrap();
    assert_eq!(created["id"].as_str(), Some(doc_id.as_str()));

    // Read
    let read = h
        .send(Method::GET, &doc_path, None, &pk_header)
        .await
        .unwrap();
    assert_eq!(read.status(), StatusCode::OK);
    let read_doc: serde_json::Value = read.json().await.unwrap();
    assert_eq!(read_doc["value"].as_str(), Some("created"));

    // Replace
    let replace = h
        .send(
            Method::PUT,
            &doc_path,
            Some(json!({ "id": doc_id, "partitionKey": pk, "value": "updated" })),
            &pk_header,
        )
        .await
        .unwrap();
    assert_eq!(replace.status(), StatusCode::OK);
    let replaced: serde_json::Value = replace.json().await.unwrap();
    assert_eq!(replaced["value"].as_str(), Some("updated"));

    // Delete
    let delete = h
        .send(Method::DELETE, &doc_path, None, &pk_header)
        .await
        .unwrap();
    assert_eq!(delete.status(), StatusCode::NO_CONTENT);

    // Read-after-delete → 404
    let gone = h
        .send(Method::GET, &doc_path, None, &pk_header)
        .await
        .unwrap();
    assert_eq!(gone.status(), StatusCode::NOT_FOUND);
}

#[tokio::test]
async fn unsigned_request_is_rejected() {
    let h = ParityHarness::start().await.unwrap();
    let resp = h.send_unsigned(Method::GET, "/dbs").await.unwrap();
    assert_eq!(resp.status(), StatusCode::UNAUTHORIZED);
}

#[tokio::test]
async fn bad_signature_is_rejected() {
    let h = ParityHarness::start().await.unwrap();
    // A syntactically valid but wrong Authorization header.
    let resp = reqwest::Client::new()
        .get(format!("{}/dbs", h.base_url()))
        .header("authorization", "type%3Dmaster%26ver%3D1.0%26sig%3Dwrong")
        .header("x-ms-date", "Sat, 21 Jul 2026 13:00:00 GMT")
        .send()
        .await
        .unwrap();
    assert_eq!(resp.status(), StatusCode::UNAUTHORIZED);
}
