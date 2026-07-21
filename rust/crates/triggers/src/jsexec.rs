//! JavaScript execution for stored procedures and triggers, backed by the
//! pure-Rust `boa_engine` interpreter. Ports the Jint-based
//! `CosmosJsContext`/`JintProgrammabilityEngine` and `TriggerEngine`.
//!
//! The `getContext()` API mirrors the Cosmos server-side JS surface:
//! `getCollection()` (self-link + document CRUD/query with node-style
//! callbacks), `getResponse()` (get/set/appendBody), and `getRequest()`
//! (get/setBody). Because `boa` native functions are synchronous while the
//! Rust `DocumentStore`/`QueryEngine` are async, the collection primitives call
//! back into Tokio via [`tokio::runtime::Handle::block_on`]; the whole script is
//! therefore expected to run on a blocking thread (`spawn_blocking`).

use std::collections::HashMap;
use std::sync::Arc;

use boa_engine::property::Attribute;
use boa_engine::{js_string, Context, JsValue, NativeFunction, Source};
use cosmos_core::error::CosmosError;
use cosmos_core::models::partition_key::PartitionKeyValue;
use cosmos_core::models::resources::JsonObject;
use cosmos_core::traits::{DocumentStore, QueryEngine, QueryOptions};
use cosmos_core::CosmosResult;
use serde_json::{json, Value};
use tokio::runtime::Handle;

/// Coarse runaway-script protection. Boa has no wall-clock timeout, so we cap
/// loop iterations instead (an infinite loop errors out rather than hanging).
/// The .NET engine uses a 5-second wall-clock timeout; this cap is a functional
/// stand-in generous enough for legitimate sprocs iterating over query results.
const LOOP_ITERATION_LIMIT: u64 = 5_000_000;

/// The shared JS prelude that defines `getContext()` on top of the low-level
/// `__*` native primitives registered by [`register_collection_natives`].
const PRELUDE: &str = r#"
var __response = { __body: undefined };
var __request = { __body: undefined };
function __appendStr(v) { return (typeof v === 'string') ? v : JSON.stringify(v); }
function __mkCollection() {
  return {
    getSelfLink: function() { return __selfLink; },
    createDocument: function(link, doc, options, callback) {
      var env = JSON.parse(__createDocument(JSON.stringify(doc)));
      if (!env.ok) { if (typeof callback === 'function') callback(env.error, null, options); return false; }
      if (typeof callback === 'function') callback(null, env.result, options);
      return true;
    },
    readDocument: function(link, options, callback) {
      var env = JSON.parse(__readDocument(String(link)));
      if (!env.ok) { if (typeof callback === 'function') callback(env.error, null, options); return false; }
      if (typeof callback === 'function') callback(null, env.result, options);
      return true;
    },
    replaceDocument: function(link, doc, options, callback) {
      var env = JSON.parse(__replaceDocument(String(link), JSON.stringify(doc)));
      if (!env.ok) { if (typeof callback === 'function') callback(env.error, null, options); return false; }
      if (typeof callback === 'function') callback(null, env.result, options);
      return true;
    },
    deleteDocument: function(link, options, callback) {
      var env = JSON.parse(__deleteDocument(String(link)));
      if (!env.ok) { if (typeof callback === 'function') callback(env.error, null, options); return false; }
      if (typeof callback === 'function') callback(null, null, options);
      return true;
    },
    queryDocuments: function(link, query, options, callback) {
      var env = JSON.parse(__queryDocuments(JSON.stringify(query)));
      if (!env.ok) { if (typeof callback === 'function') callback(env.error, null, null); return false; }
      if (typeof callback === 'function') callback(null, env.result, { count: env.result.length });
      return true;
    }
  };
}
function __mkResponse() {
  return {
    setBody: function(b) { __response.__body = b; },
    getBody: function() { return __response.__body; },
    appendBody: function(b) {
      if (__response.__body === undefined || __response.__body === null) { __response.__body = b; }
      else { __response.__body = __appendStr(__response.__body) + __appendStr(b); }
    }
  };
}
function __mkRequest() {
  return {
    getBody: function() { return __request.__body; },
    setBody: function(b) { __request.__body = b; }
  };
}
function getContext() {
  return { getCollection: __mkCollection, getResponse: __mkResponse, getRequest: __mkRequest };
}
"#;

fn ok_env(result: Value) -> String {
    json!({ "ok": true, "result": result }).to_string()
}

fn err_env(e: &CosmosError) -> String {
    json!({
        "ok": false,
        "error": { "code": e.error_code, "message": e.message }
    })
    .to_string()
}

fn require_object(value: Value) -> CosmosResult<JsonObject> {
    match value {
        Value::Object(map) => Ok(map),
        _ => Err(CosmosError::bad_request("'doc' must be a JSON object.")),
    }
}

fn extract_document_id(link: &str) -> CosmosResult<String> {
    let trimmed = link.trim();
    if trimmed.is_empty() {
        return Err(CosmosError::bad_request("Document link must be provided."));
    }
    if let Some(idx) = trimmed.to_ascii_lowercase().find("/docs/") {
        let suffix = trimmed[idx + "/docs/".len()..].trim_matches('/');
        if !suffix.is_empty() {
            return Ok(suffix.to_string());
        }
    }
    match trimmed
        .trim_matches('/')
        .split('/')
        .rfind(|s| !s.is_empty())
    {
        Some(id) => Ok(id.to_string()),
        None => Err(CosmosError::bad_request("Document link must be provided.")),
    }
}

fn parse_query_definition(value: Value) -> CosmosResult<(String, Option<HashMap<String, Value>>)> {
    match value {
        Value::String(text) if !text.trim().is_empty() => Ok((text, None)),
        Value::Object(map) => {
            let text = map
                .get("query")
                .and_then(Value::as_str)
                .filter(|s| !s.trim().is_empty())
                .ok_or_else(|| {
                    CosmosError::bad_request(
                        "'query' must be a string or an object with a 'query' property.",
                    )
                })?
                .to_string();
            let mut params = HashMap::new();
            if let Some(Value::Array(entries)) = map.get("parameters") {
                for entry in entries {
                    if let Some(name) = entry.get("name").and_then(Value::as_str) {
                        if !name.is_empty() {
                            params.insert(
                                name.to_string(),
                                entry.get("value").cloned().unwrap_or(Value::Null),
                            );
                        }
                    }
                }
            }
            Ok((
                text,
                if params.is_empty() {
                    None
                } else {
                    Some(params)
                },
            ))
        }
        _ => Err(CosmosError::bad_request(
            "'query' must be a string or an object with a 'query' property.",
        )),
    }
}

/// Registers `__createDocument`/`__readDocument`/`__replaceDocument`/
/// `__deleteDocument`/`__queryDocuments` native functions. Each takes/returns a
/// JSON string and returns a `{ok, result}`/`{ok:false, error}` envelope so the
/// JS wrapper never has to inspect thrown host exceptions.
#[allow(clippy::too_many_arguments)]
fn register_collection_natives(
    ctx: &mut Context,
    handle: Handle,
    store: Arc<dyn DocumentStore>,
    query_engine: Arc<dyn QueryEngine>,
    database_id: &str,
    container_id: &str,
    partition_key: &PartitionKeyValue,
) {
    let arg_str = |args: &[JsValue], ctx: &mut Context| -> String {
        args.first()
            .and_then(|v| v.to_string(ctx).ok())
            .map(|s| s.to_std_string_escaped())
            .unwrap_or_default()
    };

    // createDocument(docJson) -> envelope
    {
        let h = handle.clone();
        let store = store.clone();
        let db = database_id.to_string();
        let coll = container_id.to_string();
        let f = unsafe {
            NativeFunction::from_closure(move |_this, args, ctx| {
                let doc_json = arg_str(args, ctx);
                let env = (|| -> CosmosResult<Value> {
                    let value: Value = serde_json::from_str(&doc_json)
                        .map_err(|e| CosmosError::bad_request(format!("Invalid document: {e}")))?;
                    let obj = require_object(value)?;
                    let created = h.block_on(store.create_document(&db, &coll, obj, None))?;
                    Ok(Value::Object(created.to_response_body()))
                })();
                let out = match env {
                    Ok(v) => ok_env(v),
                    Err(e) => err_env(&e),
                };
                Ok(JsValue::from(js_string!(out)))
            })
        };
        ctx.register_global_callable(js_string!("__createDocument"), 1, f)
            .expect("register __createDocument");
    }

    // readDocument(link) -> envelope
    {
        let h = handle.clone();
        let store = store.clone();
        let db = database_id.to_string();
        let coll = container_id.to_string();
        let pk = partition_key.clone();
        let f = unsafe {
            NativeFunction::from_closure(move |_this, args, ctx| {
                let link = arg_str(args, ctx);
                let env = (|| -> CosmosResult<Value> {
                    let id = extract_document_id(&link)?;
                    let doc = h.block_on(store.read_document(&db, &coll, &id, &pk))?;
                    Ok(Value::Object(doc.to_response_body()))
                })();
                let out = match env {
                    Ok(v) => ok_env(v),
                    Err(e) => err_env(&e),
                };
                Ok(JsValue::from(js_string!(out)))
            })
        };
        ctx.register_global_callable(js_string!("__readDocument"), 1, f)
            .expect("register __readDocument");
    }

    // replaceDocument(link, docJson) -> envelope
    {
        let h = handle.clone();
        let store = store.clone();
        let db = database_id.to_string();
        let coll = container_id.to_string();
        let f = unsafe {
            NativeFunction::from_closure(move |_this, args, ctx| {
                let link = args
                    .first()
                    .and_then(|v| v.to_string(ctx).ok())
                    .map(|s| s.to_std_string_escaped())
                    .unwrap_or_default();
                let doc_json = args
                    .get(1)
                    .and_then(|v| v.to_string(ctx).ok())
                    .map(|s| s.to_std_string_escaped())
                    .unwrap_or_default();
                let env = (|| -> CosmosResult<Value> {
                    let id = extract_document_id(&link)?;
                    let value: Value = serde_json::from_str(&doc_json)
                        .map_err(|e| CosmosError::bad_request(format!("Invalid document: {e}")))?;
                    let obj = require_object(value)?;
                    let doc =
                        h.block_on(store.replace_document(&db, &coll, &id, obj, None, None))?;
                    Ok(Value::Object(doc.to_response_body()))
                })();
                let out = match env {
                    Ok(v) => ok_env(v),
                    Err(e) => err_env(&e),
                };
                Ok(JsValue::from(js_string!(out)))
            })
        };
        ctx.register_global_callable(js_string!("__replaceDocument"), 2, f)
            .expect("register __replaceDocument");
    }

    // deleteDocument(link) -> envelope
    {
        let h = handle.clone();
        let store = store.clone();
        let db = database_id.to_string();
        let coll = container_id.to_string();
        let pk = partition_key.clone();
        let f = unsafe {
            NativeFunction::from_closure(move |_this, args, ctx| {
                let link = arg_str(args, ctx);
                let env = (|| -> CosmosResult<Value> {
                    let id = extract_document_id(&link)?;
                    h.block_on(store.delete_document(&db, &coll, &id, &pk))?;
                    Ok(Value::Null)
                })();
                let out = match env {
                    Ok(v) => ok_env(v),
                    Err(e) => err_env(&e),
                };
                Ok(JsValue::from(js_string!(out)))
            })
        };
        ctx.register_global_callable(js_string!("__deleteDocument"), 1, f)
            .expect("register __deleteDocument");
    }

    // queryDocuments(queryJson) -> envelope (result = array)
    {
        let h = handle;
        let store_engine = query_engine;
        let db = database_id.to_string();
        let coll = container_id.to_string();
        let pk = partition_key.clone();
        let f = unsafe {
            NativeFunction::from_closure(move |_this, args, ctx| {
                let query_json = arg_str(args, ctx);
                let env = (|| -> CosmosResult<Value> {
                    let value: Value = serde_json::from_str(&query_json)
                        .map_err(|e| CosmosError::bad_request(format!("Invalid query: {e}")))?;
                    let (text, params) = parse_query_definition(value)?;
                    let options = QueryOptions {
                        partition_key: Some(pk.clone()),
                        ..Default::default()
                    };
                    let result = h.block_on(store_engine.execute_query(
                        &db,
                        &coll,
                        &text,
                        params.as_ref(),
                        Some(options),
                    ))?;
                    let rows: Vec<Value> =
                        result.resources.into_iter().map(Value::Object).collect();
                    Ok(Value::Array(rows))
                })();
                let out = match env {
                    Ok(v) => ok_env(v),
                    Err(e) => err_env(&e),
                };
                Ok(JsValue::from(js_string!(out)))
            })
        };
        ctx.register_global_callable(js_string!("__queryDocuments"), 1, f)
            .expect("register __queryDocuments");
    }
}

fn new_context() -> Context {
    let mut ctx = Context::default();
    ctx.runtime_limits_mut()
        .set_loop_iteration_limit(LOOP_ITERATION_LIMIT);
    ctx
}

fn set_global_json(ctx: &mut Context, name: &str, value: &Value) -> CosmosResult<()> {
    let js = JsValue::from_json(value, ctx)
        .map_err(|e| CosmosError::bad_request(format!("Failed to bind '{name}': {e}")))?;
    ctx.register_global_property(js_string!(name.to_string()), js, Attribute::all())
        .map_err(|e| CosmosError::bad_request(format!("Failed to bind '{name}': {e}")))?;
    Ok(())
}

fn map_js_err(prefix: &str, e: boa_engine::JsError) -> CosmosError {
    CosmosError::bad_request(format!("{prefix}: {e}"))
}

/// Executes a stored procedure body and returns its response body (if set).
#[allow(clippy::too_many_arguments)]
pub fn run_stored_procedure(
    handle: Handle,
    store: Arc<dyn DocumentStore>,
    query_engine: Arc<dyn QueryEngine>,
    database_id: &str,
    container_id: &str,
    partition_key: &PartitionKeyValue,
    body: &str,
    args: &[Value],
) -> CosmosResult<Option<Value>> {
    let mut ctx = new_context();
    register_collection_natives(
        &mut ctx,
        handle,
        store,
        query_engine,
        database_id,
        container_id,
        partition_key,
    );

    let self_link = format!("dbs/{database_id}/colls/{container_id}/");
    set_global_json(&mut ctx, "__selfLink", &Value::String(self_link))?;
    set_global_json(&mut ctx, "__args", &Value::Array(args.to_vec()))?;

    ctx.eval(Source::from_bytes(PRELUDE))
        .map_err(|e| map_js_err("Stored procedure setup failed", e))?;

    let script = format!("var __sprocFn = {body};\n__sprocFn.apply(null, __args);");
    ctx.eval(Source::from_bytes(script.as_bytes()))
        .map_err(|e| map_js_err("Stored procedure execution failed", e))?;

    let body_value = ctx
        .eval(Source::from_bytes(b"getContext().getResponse().getBody()"))
        .map_err(|e| map_js_err("Stored procedure execution failed", e))?;
    if body_value.is_undefined() || body_value.is_null() {
        return Ok(None);
    }
    let json = body_value
        .to_json(&mut ctx)
        .map_err(|e| map_js_err("Stored procedure execution failed", e))?;
    Ok(Some(json))
}

/// Executes a trigger body against a document. For pre-triggers the mutated
/// request body is returned; for post-triggers the (possibly mutated) response
/// body is returned. Trigger bodies have no collection/store access, only
/// request/response body manipulation and `getCollection().getSelfLink()`.
pub fn run_trigger(
    database_id: &str,
    container_id: &str,
    body: &str,
    document: &JsonObject,
    is_pre_trigger: bool,
) -> CosmosResult<JsonObject> {
    let mut ctx = new_context();
    let self_link = format!("dbs/{database_id}/colls/{container_id}/");
    set_global_json(&mut ctx, "__selfLink", &Value::String(self_link))?;

    // Trigger collection has only getSelfLink; the store primitives are absent.
    ctx.eval(Source::from_bytes(TRIGGER_PRELUDE))
        .map_err(|e| map_js_err("Trigger setup failed", e))?;

    let doc_value = Value::Object(document.clone());
    set_global_json(&mut ctx, "__seedBody", &doc_value)?;
    ctx.eval(Source::from_bytes(
        b"__request.__body = JSON.parse(JSON.stringify(__seedBody)); __response.__body = JSON.parse(JSON.stringify(__seedBody));",
    ))
    .map_err(|e| map_js_err("Trigger setup failed", e))?;

    let script = format!("var __triggerFn = {body};\n__triggerFn();");
    ctx.eval(Source::from_bytes(script.as_bytes()))
        .map_err(|e| map_js_err("Trigger execution failed", e))?;

    let expr: &[u8] = if is_pre_trigger {
        b"__request.__body"
    } else {
        b"__response.__body"
    };
    let result = ctx
        .eval(Source::from_bytes(expr))
        .map_err(|e| map_js_err("Trigger execution failed", e))?;
    let json = result
        .to_json(&mut ctx)
        .map_err(|e| map_js_err("Trigger execution failed", e))?;
    match json {
        Value::Object(map) => Ok(map),
        _ => Err(CosmosError::bad_request(
            "Trigger produced a non-object body.",
        )),
    }
}

/// Prelude for triggers: `getContext()` exposes only request/response bodies and
/// `getCollection().getSelfLink()` (no document CRUD/query primitives).
const TRIGGER_PRELUDE: &str = r#"
var __response = { __body: undefined };
var __request = { __body: undefined };
function getContext() {
  return {
    getCollection: function() { return { getSelfLink: function() { return __selfLink; } }; },
    getResponse: function() {
      return {
        setBody: function(b) { __response.__body = b; },
        getBody: function() { return __response.__body; }
      };
    },
    getRequest: function() {
      return {
        setBody: function(b) { __request.__body = b; },
        getBody: function() { return __request.__body; }
      };
    }
  };
}
"#;
