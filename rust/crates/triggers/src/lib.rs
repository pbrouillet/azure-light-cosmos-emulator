//! Triggers and JavaScript programmability. Ports the .NET `Triggers` project
//! and the sproc/UDF/trigger execution surface
//! (`JintProgrammabilityEngine`, `CosmosJsContext`, `TriggerEngine`).
//!
//! [`JsProgrammabilityEngine`] implements [`cosmos_core::traits::ProgrammabilityEngine`]
//! (full CRUD for stored procedures, triggers and UDFs, plus stored-procedure
//! execution) using the pure-Rust `boa_engine` JavaScript interpreter, and adds
//! inherent pre/post trigger execution helpers mirroring the .NET `TriggerEngine`.
//!
//! Record metadata (sproc/trigger/UDF bodies) is held in-memory, keyed by
//! `(database, container, id)`. Unlike the .NET engine (which persists records
//! in `cosmos_sprocs`/`cosmos_triggers`/`cosmos_udfs` tables), this port does
//! **not** persist programmability metadata across restarts — a documented gap.

mod jsexec;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use cosmos_core::error::CosmosError;
use cosmos_core::models::feed::FeedResponse;
use cosmos_core::models::partition_key::PartitionKeyValue;
use cosmos_core::models::programmability::{
    StoredProcedure, Trigger, TriggerOperation, TriggerType, UserDefinedFunction,
};
use cosmos_core::models::resources::JsonObject;
use cosmos_core::traits::{DocumentStore, ProgrammabilityEngine, QueryEngine};
use cosmos_core::CosmosResult;
use serde_json::Value;

/// Kinds of programmability resources, mirroring the .NET model.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProgrammabilityKind {
    StoredProcedure,
    Trigger,
    UserDefinedFunction,
}

type RecordMap<T> = Mutex<HashMap<String, T>>;

fn record_key(database_id: &str, container_id: &str, id: &str) -> String {
    format!("{database_id}\u{0}{container_id}\u{0}{id}")
}

/// JavaScript programmability engine backed by `boa_engine` and an in-memory
/// record store. Ports `JintProgrammabilityEngine` + `TriggerEngine`.
pub struct JsProgrammabilityEngine {
    store: Arc<dyn DocumentStore>,
    query_engine: Arc<dyn QueryEngine>,
    sprocs: RecordMap<StoredProcedure>,
    triggers: RecordMap<Trigger>,
    udfs: RecordMap<UserDefinedFunction>,
}

impl JsProgrammabilityEngine {
    pub fn new(store: Arc<dyn DocumentStore>, query_engine: Arc<dyn QueryEngine>) -> Self {
        Self {
            store,
            query_engine,
            sprocs: Mutex::new(HashMap::new()),
            triggers: Mutex::new(HashMap::new()),
            udfs: Mutex::new(HashMap::new()),
        }
    }

    fn sorted<T>(map: &RecordMap<T>, database_id: &str, container_id: &str) -> Vec<(String, T)>
    where
        T: Clone + HasIdentity,
    {
        let guard = map.lock().expect("record lock poisoned");
        let mut items: Vec<(String, T)> = guard
            .values()
            .filter(|v| v.database_id() == database_id && v.container_id() == container_id)
            .map(|v| (v.id().to_string(), v.clone()))
            .collect();
        items.sort_by(|a, b| a.0.cmp(&b.0));
        items
    }

    /// Executes pre-triggers before a document operation. Pre-triggers may
    /// mutate the request body; the (possibly mutated) document is returned.
    /// Ports `TriggerEngine.ExecutePreTriggersAsync`.
    pub async fn execute_pre_triggers(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        operation: TriggerOperation,
        trigger_ids: &[String],
    ) -> CosmosResult<JsonObject> {
        let mut current = document;
        for trigger_id in trigger_ids {
            let trigger = self
                .get_trigger(database_id, container_id, trigger_id)
                .await?;
            if trigger.trigger_type != TriggerType::Pre {
                return Err(CosmosError::bad_request(format!(
                    "Trigger '{trigger_id}' is not a pre-trigger."
                )));
            }
            if trigger.trigger_operation != TriggerOperation::All
                && trigger.trigger_operation != operation
            {
                continue;
            }
            let db = database_id.to_string();
            let coll = container_id.to_string();
            let body = trigger.body.clone();
            let doc = current;
            current = tokio::task::spawn_blocking(move || {
                jsexec::run_trigger(&db, &coll, &body, &doc, true)
            })
            .await
            .map_err(|e| {
                CosmosError::internal_server_error(format!("Trigger task failed: {e}"))
            })??;
        }
        Ok(current)
    }

    /// Executes post-triggers after a document operation. Post-triggers observe
    /// the response body (mutations are not persisted).
    /// Ports `TriggerEngine.ExecutePostTriggersAsync`.
    pub async fn execute_post_triggers(
        &self,
        database_id: &str,
        container_id: &str,
        document: JsonObject,
        operation: TriggerOperation,
        trigger_ids: &[String],
    ) -> CosmosResult<()> {
        for trigger_id in trigger_ids {
            let trigger = self
                .get_trigger(database_id, container_id, trigger_id)
                .await?;
            if trigger.trigger_type != TriggerType::Post {
                return Err(CosmosError::bad_request(format!(
                    "Trigger '{trigger_id}' is not a post-trigger."
                )));
            }
            if trigger.trigger_operation != TriggerOperation::All
                && trigger.trigger_operation != operation
            {
                continue;
            }
            let db = database_id.to_string();
            let coll = container_id.to_string();
            let body = trigger.body.clone();
            let doc = document.clone();
            tokio::task::spawn_blocking(move || {
                jsexec::run_trigger(&db, &coll, &body, &doc, false)
            })
            .await
            .map_err(|e| {
                CosmosError::internal_server_error(format!("Trigger task failed: {e}"))
            })??;
        }
        Ok(())
    }
}

/// Common identity accessors so the generic list/sort helper works across the
/// three record types.
trait HasIdentity {
    fn id(&self) -> &str;
    fn database_id(&self) -> &str;
    fn container_id(&self) -> &str;
}

impl HasIdentity for StoredProcedure {
    fn id(&self) -> &str {
        &self.id
    }
    fn database_id(&self) -> &str {
        &self.database_id
    }
    fn container_id(&self) -> &str {
        &self.container_id
    }
}

impl HasIdentity for Trigger {
    fn id(&self) -> &str {
        &self.id
    }
    fn database_id(&self) -> &str {
        &self.database_id
    }
    fn container_id(&self) -> &str {
        &self.container_id
    }
}

impl HasIdentity for UserDefinedFunction {
    fn id(&self) -> &str {
        &self.id
    }
    fn database_id(&self) -> &str {
        &self.database_id
    }
    fn container_id(&self) -> &str {
        &self.container_id
    }
}

#[async_trait]
impl ProgrammabilityEngine for JsProgrammabilityEngine {
    async fn create_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        mut sproc: StoredProcedure,
    ) -> CosmosResult<StoredProcedure> {
        let key = record_key(database_id, container_id, &sproc.id);
        let mut guard = self.sprocs.lock().expect("record lock poisoned");
        if guard.contains_key(&key) {
            return Err(CosmosError::conflict("StoredProcedure", &sproc.id));
        }
        sproc.database_id = database_id.to_string();
        sproc.container_id = container_id.to_string();
        sproc.self_link = format!(
            "dbs/{database_id}/colls/{container_id}/sprocs/{}/",
            sproc.id
        );
        guard.insert(key, sproc.clone());
        Ok(sproc)
    }

    async fn get_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc_id: &str,
    ) -> CosmosResult<StoredProcedure> {
        let key = record_key(database_id, container_id, sproc_id);
        self.sprocs
            .lock()
            .expect("record lock poisoned")
            .get(&key)
            .cloned()
            .ok_or_else(|| CosmosError::not_found("StoredProcedure", sproc_id))
    }

    async fn list_stored_procedures(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<StoredProcedure>> {
        let items = Self::sorted(&self.sprocs, database_id, container_id)
            .into_iter()
            .map(|(_, v)| v)
            .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc: StoredProcedure,
    ) -> CosmosResult<StoredProcedure> {
        let key = record_key(database_id, container_id, &sproc.id);
        let mut guard = self.sprocs.lock().expect("record lock poisoned");
        let existing = guard
            .get(&key)
            .ok_or_else(|| CosmosError::not_found("StoredProcedure", &sproc.id))?;
        let updated = StoredProcedure {
            id: sproc.id.clone(),
            rid: existing.rid.clone(),
            self_link: format!(
                "dbs/{database_id}/colls/{container_id}/sprocs/{}/",
                sproc.id
            ),
            etag: cosmos_core::ids::etag(),
            timestamp: chrono::Utc::now().timestamp(),
            database_id: database_id.to_string(),
            container_id: container_id.to_string(),
            body: sproc.body,
        };
        guard.insert(key, updated.clone());
        Ok(updated)
    }

    async fn delete_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc_id: &str,
    ) -> CosmosResult<()> {
        let key = record_key(database_id, container_id, sproc_id);
        let mut guard = self.sprocs.lock().expect("record lock poisoned");
        if guard.remove(&key).is_none() {
            return Err(CosmosError::not_found("StoredProcedure", sproc_id));
        }
        Ok(())
    }

    async fn execute_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc_id: &str,
        args: &[Value],
        partition_key: &PartitionKeyValue,
    ) -> CosmosResult<Option<Value>> {
        let sproc = self
            .get_stored_procedure(database_id, container_id, sproc_id)
            .await?;
        let handle = tokio::runtime::Handle::current();
        let store = self.store.clone();
        let query_engine = self.query_engine.clone();
        let db = database_id.to_string();
        let coll = container_id.to_string();
        let pk = partition_key.clone();
        let args = args.to_vec();
        let body = sproc.body.clone();
        tokio::task::spawn_blocking(move || {
            jsexec::run_stored_procedure(handle, store, query_engine, &db, &coll, &pk, &body, &args)
        })
        .await
        .map_err(|e| CosmosError::internal_server_error(format!("Sproc task failed: {e}")))?
    }

    async fn create_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        mut trigger: Trigger,
    ) -> CosmosResult<Trigger> {
        let key = record_key(database_id, container_id, &trigger.id);
        let mut guard = self.triggers.lock().expect("record lock poisoned");
        if guard.contains_key(&key) {
            return Err(CosmosError::conflict("Trigger", &trigger.id));
        }
        trigger.database_id = database_id.to_string();
        trigger.container_id = container_id.to_string();
        trigger.self_link = format!(
            "dbs/{database_id}/colls/{container_id}/triggers/{}/",
            trigger.id
        );
        guard.insert(key, trigger.clone());
        Ok(trigger)
    }

    async fn get_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        trigger_id: &str,
    ) -> CosmosResult<Trigger> {
        let key = record_key(database_id, container_id, trigger_id);
        self.triggers
            .lock()
            .expect("record lock poisoned")
            .get(&key)
            .cloned()
            .ok_or_else(|| CosmosError::not_found("Trigger", trigger_id))
    }

    async fn list_triggers(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<Trigger>> {
        let items = Self::sorted(&self.triggers, database_id, container_id)
            .into_iter()
            .map(|(_, v)| v)
            .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        trigger: Trigger,
    ) -> CosmosResult<Trigger> {
        let key = record_key(database_id, container_id, &trigger.id);
        let mut guard = self.triggers.lock().expect("record lock poisoned");
        let existing = guard
            .get(&key)
            .ok_or_else(|| CosmosError::not_found("Trigger", &trigger.id))?;
        let updated = Trigger {
            id: trigger.id.clone(),
            rid: existing.rid.clone(),
            self_link: format!(
                "dbs/{database_id}/colls/{container_id}/triggers/{}/",
                trigger.id
            ),
            etag: cosmos_core::ids::etag(),
            timestamp: chrono::Utc::now().timestamp(),
            database_id: database_id.to_string(),
            container_id: container_id.to_string(),
            body: trigger.body,
            trigger_type: trigger.trigger_type,
            trigger_operation: trigger.trigger_operation,
        };
        guard.insert(key, updated.clone());
        Ok(updated)
    }

    async fn delete_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        trigger_id: &str,
    ) -> CosmosResult<()> {
        let key = record_key(database_id, container_id, trigger_id);
        let mut guard = self.triggers.lock().expect("record lock poisoned");
        if guard.remove(&key).is_none() {
            return Err(CosmosError::not_found("Trigger", trigger_id));
        }
        Ok(())
    }

    async fn create_udf(
        &self,
        database_id: &str,
        container_id: &str,
        mut udf: UserDefinedFunction,
    ) -> CosmosResult<UserDefinedFunction> {
        let key = record_key(database_id, container_id, &udf.id);
        let mut guard = self.udfs.lock().expect("record lock poisoned");
        if guard.contains_key(&key) {
            return Err(CosmosError::conflict("UserDefinedFunction", &udf.id));
        }
        udf.database_id = database_id.to_string();
        udf.container_id = container_id.to_string();
        udf.self_link = format!("dbs/{database_id}/colls/{container_id}/udfs/{}/", udf.id);
        guard.insert(key, udf.clone());
        Ok(udf)
    }

    async fn get_udf(
        &self,
        database_id: &str,
        container_id: &str,
        udf_id: &str,
    ) -> CosmosResult<UserDefinedFunction> {
        let key = record_key(database_id, container_id, udf_id);
        self.udfs
            .lock()
            .expect("record lock poisoned")
            .get(&key)
            .cloned()
            .ok_or_else(|| CosmosError::not_found("UserDefinedFunction", udf_id))
    }

    async fn list_udfs(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<UserDefinedFunction>> {
        let items = Self::sorted(&self.udfs, database_id, container_id)
            .into_iter()
            .map(|(_, v)| v)
            .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_udf(
        &self,
        database_id: &str,
        container_id: &str,
        udf: UserDefinedFunction,
    ) -> CosmosResult<UserDefinedFunction> {
        let key = record_key(database_id, container_id, &udf.id);
        let mut guard = self.udfs.lock().expect("record lock poisoned");
        let existing = guard
            .get(&key)
            .ok_or_else(|| CosmosError::not_found("UserDefinedFunction", &udf.id))?;
        let updated = UserDefinedFunction {
            id: udf.id.clone(),
            rid: existing.rid.clone(),
            self_link: format!("dbs/{database_id}/colls/{container_id}/udfs/{}/", udf.id),
            etag: cosmos_core::ids::etag(),
            timestamp: chrono::Utc::now().timestamp(),
            database_id: database_id.to_string(),
            container_id: container_id.to_string(),
            body: udf.body,
        };
        guard.insert(key, updated.clone());
        Ok(updated)
    }

    async fn delete_udf(
        &self,
        database_id: &str,
        container_id: &str,
        udf_id: &str,
    ) -> CosmosResult<()> {
        let key = record_key(database_id, container_id, udf_id);
        let mut guard = self.udfs.lock().expect("record lock poisoned");
        if guard.remove(&key).is_none() {
            return Err(CosmosError::not_found("UserDefinedFunction", udf_id));
        }
        Ok(())
    }
}
