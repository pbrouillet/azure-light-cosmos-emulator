//! Integration tests for the JS programmability engine (sprocs/triggers/UDFs).

use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use cosmos_core::models::partition_key::{PartitionKeyDefinition, PartitionKeyValue};
use cosmos_core::models::programmability::{
    StoredProcedure, Trigger, TriggerOperation, TriggerType, UserDefinedFunction,
};
use cosmos_core::models::resources::CosmosContainer;
use cosmos_core::traits::{DocumentStore, ProgrammabilityEngine};
use cosmos_query::{SqlQueryEngine, UdfResolver};
use cosmos_storage::inmemory::InMemoryDocumentStore;
use cosmos_storage::{ProgrammabilityRecordStore, SqliteDocumentStore};
use cosmos_triggers::JsProgrammabilityEngine;
use serde_json::json;

const DB: &str = "db1";
const COLL: &str = "coll1";

async fn setup() -> (Arc<dyn DocumentStore>, JsProgrammabilityEngine) {
    let store: Arc<dyn DocumentStore> = Arc::new(InMemoryDocumentStore::new());
    store.create_database(DB).await.unwrap();
    store
        .create_container(
            DB,
            CosmosContainer::new(DB, COLL, PartitionKeyDefinition::new(vec!["/pk".into()])),
        )
        .await
        .unwrap();
    let query_engine = Arc::new(SqlQueryEngine::new(store.clone()));
    let engine = JsProgrammabilityEngine::new(store.clone(), query_engine);
    (store, engine)
}

fn pk() -> PartitionKeyValue {
    PartitionKeyValue::single(json!("p1"))
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sproc_set_body_returns_value() {
    let (_store, engine) = setup().await;
    engine
        .create_stored_procedure(
            DB,
            COLL,
            StoredProcedure::new(
                DB,
                COLL,
                "echo",
                "function(a, b) { getContext().getResponse().setBody(a + b); }",
            ),
        )
        .await
        .unwrap();

    let result = engine
        .execute_stored_procedure(DB, COLL, "echo", &[json!(2), json!(40)], &pk())
        .await
        .unwrap();
    assert_eq!(result, Some(json!(42)));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sproc_create_and_read_document() {
    let (store, engine) = setup().await;
    let body = r#"function(doc) {
        var ctx = getContext();
        var coll = ctx.getCollection();
        coll.createDocument(coll.getSelfLink(), doc, {}, function(err, created) {
            if (err) { throw new Error(err.message); }
            ctx.getResponse().setBody(created);
        });
    }"#;
    engine
        .create_stored_procedure(DB, COLL, StoredProcedure::new(DB, COLL, "create", body))
        .await
        .unwrap();

    let doc = json!({"id": "d1", "pk": "p1", "val": 7});
    let result = engine
        .execute_stored_procedure(DB, COLL, "create", &[doc], &pk())
        .await
        .unwrap()
        .expect("sproc returns body");
    assert_eq!(result["id"], json!("d1"));
    assert_eq!(result["val"], json!(7));

    let read = store
        .read_document(DB, COLL, "d1", &pk())
        .await
        .expect("document persisted");
    assert_eq!(read.id, "d1");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sproc_query_documents() {
    let (store, engine) = setup().await;
    for i in 0..3 {
        store
            .create_document(
                DB,
                COLL,
                json!({"id": format!("q{i}"), "pk": "p1", "n": i})
                    .as_object()
                    .unwrap()
                    .clone(),
                None,
            )
            .await
            .unwrap();
    }
    let body = r#"function() {
        var ctx = getContext();
        var coll = ctx.getCollection();
        coll.queryDocuments(coll.getSelfLink(), "SELECT * FROM c", {}, function(err, docs, opts) {
            if (err) { throw new Error(err.message); }
            ctx.getResponse().setBody(opts.count);
        });
    }"#;
    engine
        .create_stored_procedure(DB, COLL, StoredProcedure::new(DB, COLL, "count", body))
        .await
        .unwrap();

    let result = engine
        .execute_stored_procedure(DB, COLL, "count", &[], &pk())
        .await
        .unwrap();
    assert_eq!(result, Some(json!(3)));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sproc_query_callback_returns_options_and_append_body_matches_cosmos_context() {
    let (store, engine) = setup().await;
    store
        .create_document(
            DB,
            COLL,
            json!({"id": "qo1", "pk": "p1", "n": 10})
                .as_object()
                .unwrap()
                .clone(),
            None,
        )
        .await
        .unwrap();
    let body = r#"function() {
        var ctx = getContext();
        var coll = ctx.getCollection();
        coll.queryDocuments(
            coll.getSelfLink(),
            { query: "SELECT * FROM c WHERE c.id = @id", parameters: [{ name: "@id", value: "qo1" }] },
            { marker: "kept" },
            function(err, docs, opts) {
                if (err) { throw new Error(err.message); }
                var response = ctx.getResponse();
                response.setBody("count=");
                response.appendBody({ count: opts.count, marker: opts.options.marker, id: docs[0].id });
            });
    }"#;
    engine
        .create_stored_procedure(
            DB,
            COLL,
            StoredProcedure::new(DB, COLL, "queryOptions", body),
        )
        .await
        .unwrap();

    let result = engine
        .execute_stored_procedure(DB, COLL, "queryOptions", &[], &pk())
        .await
        .unwrap();
    assert_eq!(
        result,
        Some(json!(
            "count={\"count\":1,\"marker\":\"kept\",\"id\":\"qo1\"}"
        ))
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sproc_read_replace_and_delete_document() {
    let (store, engine) = setup().await;
    store
        .create_document(
            DB,
            COLL,
            json!({"id": "rd1", "pk": "p1", "value": 1})
                .as_object()
                .unwrap()
                .clone(),
            None,
        )
        .await
        .unwrap();
    let body = r#"function() {
        var ctx = getContext();
        var coll = ctx.getCollection();
        var link = coll.getSelfLink() + "docs/rd1";
        coll.readDocument(link, {}, function(err, doc) {
            if (err) { throw new Error(err.message); }
            doc.value = 2;
            coll.replaceDocument(link, doc, {}, function(err2, replaced) {
                if (err2) { throw new Error(err2.message); }
                coll.deleteDocument(link, {}, function(err3) {
                    if (err3) { throw new Error(err3.message); }
                    ctx.getResponse().setBody(replaced.value);
                });
            });
        });
    }"#;
    engine
        .create_stored_procedure(DB, COLL, StoredProcedure::new(DB, COLL, "mutate", body))
        .await
        .unwrap();

    let result = engine
        .execute_stored_procedure(DB, COLL, "mutate", &[], &pk())
        .await
        .unwrap();
    assert_eq!(result, Some(json!(2)));
    assert_eq!(
        store
            .read_document(DB, COLL, "rd1", &pk())
            .await
            .unwrap_err()
            .status_code,
        404
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn pre_trigger_mutates_request_body() {
    let (_store, engine) = setup().await;
    let body = r#"function() {
        var req = getContext().getRequest();
        var doc = req.getBody();
        doc.stamped = true;
        req.setBody(doc);
    }"#;
    engine
        .create_trigger(
            DB,
            COLL,
            Trigger::new(
                DB,
                COLL,
                "stamp",
                body,
                TriggerType::Pre,
                TriggerOperation::Create,
            ),
        )
        .await
        .unwrap();

    let doc = json!({"id": "x", "pk": "p1"}).as_object().unwrap().clone();
    let out = engine
        .execute_pre_triggers(DB, COLL, doc, TriggerOperation::Create, &["stamp".into()])
        .await
        .unwrap();
    assert_eq!(out.get("stamped"), Some(&json!(true)));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn pre_trigger_skipped_for_non_matching_operation() {
    let (_store, engine) = setup().await;
    let body = "function() { var r = getContext().getRequest(); var d = r.getBody(); d.touched = true; r.setBody(d); }";
    engine
        .create_trigger(
            DB,
            COLL,
            Trigger::new(
                DB,
                COLL,
                "onlyDelete",
                body,
                TriggerType::Pre,
                TriggerOperation::Delete,
            ),
        )
        .await
        .unwrap();

    let doc = json!({"id": "x", "pk": "p1"}).as_object().unwrap().clone();
    let out = engine
        .execute_pre_triggers(
            DB,
            COLL,
            doc,
            TriggerOperation::Create,
            &["onlyDelete".into()],
        )
        .await
        .unwrap();
    assert_eq!(out.get("touched"), None);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn post_trigger_runs() {
    let (_store, engine) = setup().await;
    engine
        .create_trigger(
            DB,
            COLL,
            Trigger::new(
                DB,
                COLL,
                "log",
                "function() { var b = getContext().getResponse().getBody(); if (!b.id) { throw new Error('no id'); } }",
                TriggerType::Post,
                TriggerOperation::All,
            ),
        )
        .await
        .unwrap();

    let doc = json!({"id": "y", "pk": "p1"}).as_object().unwrap().clone();
    engine
        .execute_post_triggers(DB, COLL, doc, TriggerOperation::Create, &["log".into()])
        .await
        .expect("post trigger succeeds");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn pre_trigger_wrong_type_errors() {
    let (_store, engine) = setup().await;
    engine
        .create_trigger(
            DB,
            COLL,
            Trigger::new(
                DB,
                COLL,
                "postish",
                "function() {}",
                TriggerType::Post,
                TriggerOperation::All,
            ),
        )
        .await
        .unwrap();

    let doc = json!({"id": "z", "pk": "p1"}).as_object().unwrap().clone();
    let err = engine
        .execute_pre_triggers(DB, COLL, doc, TriggerOperation::Create, &["postish".into()])
        .await
        .unwrap_err();
    assert_eq!(err.status_code, 400);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn duplicate_sproc_conflicts() {
    let (_store, engine) = setup().await;
    engine
        .create_stored_procedure(
            DB,
            COLL,
            StoredProcedure::new(DB, COLL, "dup", "function(){}"),
        )
        .await
        .unwrap();
    let err = engine
        .create_stored_procedure(
            DB,
            COLL,
            StoredProcedure::new(DB, COLL, "dup", "function(){}"),
        )
        .await
        .unwrap_err();
    assert_eq!(err.status_code, 409);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sproc_crud_lifecycle() {
    let (_store, engine) = setup().await;
    engine
        .create_stored_procedure(
            DB,
            COLL,
            StoredProcedure::new(DB, COLL, "s1", "function(){}"),
        )
        .await
        .unwrap();
    let got = engine.get_stored_procedure(DB, COLL, "s1").await.unwrap();
    assert_eq!(got.self_link, "dbs/db1/colls/coll1/sprocs/s1/");

    let replaced = engine
        .replace_stored_procedure(
            DB,
            COLL,
            StoredProcedure::new(DB, COLL, "s1", "function(){ /* v2 */ }"),
        )
        .await
        .unwrap();
    assert_eq!(replaced.rid, got.rid);
    assert!(replaced.body.contains("v2"));

    let list = engine.list_stored_procedures(DB, COLL).await.unwrap();
    assert_eq!(list.resources.len(), 1);

    engine
        .delete_stored_procedure(DB, COLL, "s1")
        .await
        .unwrap();
    let err = engine
        .get_stored_procedure(DB, COLL, "s1")
        .await
        .unwrap_err();
    assert_eq!(err.status_code, 404);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn udf_crud_lifecycle() {
    let (_store, engine) = setup().await;
    engine
        .create_udf(
            DB,
            COLL,
            UserDefinedFunction::new(DB, COLL, "tax", "function(v){ return v * 1.1; }"),
        )
        .await
        .unwrap();
    let got = engine.get_udf(DB, COLL, "tax").await.unwrap();
    assert_eq!(got.self_link, "dbs/db1/colls/coll1/udfs/tax/");
    let list = engine.list_udfs(DB, COLL).await.unwrap();
    assert_eq!(list.resources.len(), 1);
    engine.delete_udf(DB, COLL, "tax").await.unwrap();
    assert_eq!(
        engine
            .get_udf(DB, COLL, "tax")
            .await
            .unwrap_err()
            .status_code,
        404
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn udf_resolver_executes_registered_body() {
    let (_store, engine) = setup().await;
    engine
        .create_udf(
            DB,
            COLL,
            UserDefinedFunction::new(DB, COLL, "tax", "function tax(v){ return v * 1.1; }"),
        )
        .await
        .unwrap();

    let result = UdfResolver::eval(&engine, DB, COLL, "tax", &[json!(10)]);
    assert_eq!(result, Some(json!(11.0)));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sproc_runaway_loop_is_terminated() {
    let (_store, engine) = setup().await;
    engine
        .create_stored_procedure(
            DB,
            COLL,
            StoredProcedure::new(DB, COLL, "loop", "function(){ while(true){} }"),
        )
        .await
        .unwrap();
    let err = engine
        .execute_stored_procedure(DB, COLL, "loop", &[], &pk())
        .await
        .unwrap_err();
    assert_eq!(err.status_code, 400);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn execute_missing_sproc_not_found() {
    let (_store, engine) = setup().await;
    let err = engine
        .execute_stored_procedure(DB, COLL, "nope", &[], &pk())
        .await
        .unwrap_err();
    assert_eq!(err.status_code, 404);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sqlite_record_store_persists_across_reopen() {
    let unique = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let dir = std::path::PathBuf::from(format!("target/programmability-persist-{unique}"));
    let _ = std::fs::remove_dir_all(&dir);

    {
        let sqlite = SqliteDocumentStore::open(&dir).unwrap();
        sqlite.create_database(DB).await.unwrap();
        sqlite
            .create_container(
                DB,
                CosmosContainer::new(DB, COLL, PartitionKeyDefinition::new(vec!["/pk".into()])),
            )
            .await
            .unwrap();
        let record_store: Arc<dyn ProgrammabilityRecordStore> =
            Arc::new(sqlite.programmability_store());
        let store: Arc<dyn DocumentStore> = Arc::new(sqlite);
        let query_engine = Arc::new(SqlQueryEngine::new(store.clone()));
        let engine = JsProgrammabilityEngine::with_record_store(store, query_engine, record_store);
        engine
            .create_stored_procedure(
                DB,
                COLL,
                StoredProcedure::new(
                    DB,
                    COLL,
                    "persisted",
                    "function(){ getContext().getResponse().setBody('ok'); }",
                ),
            )
            .await
            .unwrap();
    }

    {
        let sqlite = SqliteDocumentStore::open(&dir).unwrap();
        let record_store: Arc<dyn ProgrammabilityRecordStore> =
            Arc::new(sqlite.programmability_store());
        let store: Arc<dyn DocumentStore> = Arc::new(sqlite);
        let query_engine = Arc::new(SqlQueryEngine::new(store.clone()));
        let engine = JsProgrammabilityEngine::with_record_store(store, query_engine, record_store);
        let sproc = engine
            .get_stored_procedure(DB, COLL, "persisted")
            .await
            .unwrap();
        assert_eq!(
            sproc.body,
            "function(){ getContext().getResponse().setBody('ok'); }"
        );
    }

    let _ = std::fs::remove_dir_all(&dir);
}
