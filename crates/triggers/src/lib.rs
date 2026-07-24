//! Triggers and JavaScript programmability. Ports the .NET `Triggers` project
//! and the sproc/UDF/trigger execution surface
//! (`JintProgrammabilityEngine`, `CosmosJsContext`, `TriggerEngine`).
//!
//! [`JsProgrammabilityEngine`] implements [`cosmos_core::traits::ProgrammabilityEngine`]
//! (full CRUD for stored procedures, triggers and UDFs, plus stored-procedure
//! execution) using the pure-Rust `boa_engine` JavaScript interpreter, and adds
//! inherent pre/post trigger execution helpers mirroring the .NET `TriggerEngine`.
//!
mod jsexec;

use std::sync::Arc;

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
use cosmos_query::UdfResolver;
use cosmos_storage::{
    make_record_key, InMemoryProgrammabilityRecordStore, ProgrammabilityRecord,
    ProgrammabilityRecordStore, ProgrammabilityTable,
};
use serde_json::Value;
use tokio::runtime::Handle;

/// Kinds of programmability resources, mirroring the .NET model.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProgrammabilityKind {
    StoredProcedure,
    Trigger,
    UserDefinedFunction,
}

/// JavaScript programmability engine backed by `boa_engine` and a pluggable
/// record store. Ports `JintProgrammabilityEngine` + `TriggerEngine`.
pub struct JsProgrammabilityEngine {
    store: Arc<dyn DocumentStore>,
    query_engine: Arc<dyn QueryEngine>,
    record_store: Arc<dyn ProgrammabilityRecordStore>,
}

impl JsProgrammabilityEngine {
    pub fn new(store: Arc<dyn DocumentStore>, query_engine: Arc<dyn QueryEngine>) -> Self {
        Self::with_record_store(
            store,
            query_engine,
            Arc::new(InMemoryProgrammabilityRecordStore::new()),
        )
    }

    pub fn with_record_store(
        store: Arc<dyn DocumentStore>,
        query_engine: Arc<dyn QueryEngine>,
        record_store: Arc<dyn ProgrammabilityRecordStore>,
    ) -> Self {
        Self {
            store,
            query_engine,
            record_store,
        }
    }

    fn sorted<T>(
        records: impl IntoIterator<Item = T>,
        database_id: &str,
        container_id: &str,
    ) -> Vec<T>
    where
        T: HasIdentity,
    {
        let mut items: Vec<T> = records
            .into_iter()
            .filter(|v| v.database_id() == database_id && v.container_id() == container_id)
            .collect();
        items.sort_by(|a, b| a.id().cmp(b.id()));
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

impl UdfResolver for JsProgrammabilityEngine {
    fn eval(
        &self,
        database_id: &str,
        container_id: &str,
        name: &str,
        args: &[Value],
    ) -> Option<Value> {
        let key = make_record_key(database_id, container_id, name);
        let record_store = self.record_store.clone();
        let record = block_on_record_lookup(record_store, key)?;
        let ProgrammabilityRecord::UserDefinedFunction(udf) = record else {
            return None;
        };
        jsexec::run_udf(&udf.id, &udf.body, args).ok().flatten()
    }
}

fn block_on_record_lookup(
    record_store: Arc<dyn ProgrammabilityRecordStore>,
    key: String,
) -> Option<ProgrammabilityRecord> {
    let fut = async move {
        record_store
            .select_record(ProgrammabilityTable::UserDefinedFunctions, &key)
            .await
            .ok()
            .flatten()
    };

    if let Ok(handle) = Handle::try_current() {
        std::thread::spawn(move || handle.block_on(fut))
            .join()
            .ok()
            .flatten()
    } else {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .ok()?
            .block_on(fut)
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
        let key = make_record_key(database_id, container_id, &sproc.id);
        if self
            .record_store
            .select_record(ProgrammabilityTable::StoredProcedures, &key)
            .await?
            .is_some()
        {
            return Err(CosmosError::conflict("StoredProcedure", &sproc.id));
        }
        sproc.database_id = database_id.to_string();
        sproc.container_id = container_id.to_string();
        sproc.self_link = format!(
            "dbs/{database_id}/colls/{container_id}/sprocs/{}/",
            sproc.id
        );
        self.record_store
            .create_record(
                ProgrammabilityTable::StoredProcedures,
                &key,
                ProgrammabilityRecord::StoredProcedure(sproc.clone()),
            )
            .await?;
        Ok(sproc)
    }

    async fn get_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc_id: &str,
    ) -> CosmosResult<StoredProcedure> {
        let key = make_record_key(database_id, container_id, sproc_id);
        match self
            .record_store
            .select_record(ProgrammabilityTable::StoredProcedures, &key)
            .await?
        {
            Some(ProgrammabilityRecord::StoredProcedure(s)) => Ok(s),
            _ => Err(CosmosError::not_found("StoredProcedure", sproc_id)),
        }
    }

    async fn list_stored_procedures(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<StoredProcedure>> {
        let records = self
            .record_store
            .select_table_records(ProgrammabilityTable::StoredProcedures)
            .await?;
        let items = Self::sorted(
            records.into_iter().filter_map(|record| match record {
                ProgrammabilityRecord::StoredProcedure(s) => Some(s),
                _ => None,
            }),
            database_id,
            container_id,
        )
        .into_iter()
        .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc: StoredProcedure,
    ) -> CosmosResult<StoredProcedure> {
        let existing = self
            .get_stored_procedure(database_id, container_id, &sproc.id)
            .await?;
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
        let key = make_record_key(database_id, container_id, &updated.id);
        self.record_store
            .upsert_record(
                ProgrammabilityTable::StoredProcedures,
                &key,
                ProgrammabilityRecord::StoredProcedure(updated.clone()),
            )
            .await?;
        Ok(updated)
    }

    async fn delete_stored_procedure(
        &self,
        database_id: &str,
        container_id: &str,
        sproc_id: &str,
    ) -> CosmosResult<()> {
        let key = make_record_key(database_id, container_id, sproc_id);
        self.record_store
            .delete_record(
                ProgrammabilityTable::StoredProcedures,
                &key,
                "StoredProcedure",
                sproc_id,
            )
            .await
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
        let key = make_record_key(database_id, container_id, &trigger.id);
        if self
            .record_store
            .select_record(ProgrammabilityTable::Triggers, &key)
            .await?
            .is_some()
        {
            return Err(CosmosError::conflict("Trigger", &trigger.id));
        }
        trigger.database_id = database_id.to_string();
        trigger.container_id = container_id.to_string();
        trigger.self_link = format!(
            "dbs/{database_id}/colls/{container_id}/triggers/{}/",
            trigger.id
        );
        self.record_store
            .create_record(
                ProgrammabilityTable::Triggers,
                &key,
                ProgrammabilityRecord::Trigger(trigger.clone()),
            )
            .await?;
        Ok(trigger)
    }

    async fn get_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        trigger_id: &str,
    ) -> CosmosResult<Trigger> {
        let key = make_record_key(database_id, container_id, trigger_id);
        match self
            .record_store
            .select_record(ProgrammabilityTable::Triggers, &key)
            .await?
        {
            Some(ProgrammabilityRecord::Trigger(t)) => Ok(t),
            _ => Err(CosmosError::not_found("Trigger", trigger_id)),
        }
    }

    async fn list_triggers(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<Trigger>> {
        let records = self
            .record_store
            .select_table_records(ProgrammabilityTable::Triggers)
            .await?;
        let items = Self::sorted(
            records.into_iter().filter_map(|record| match record {
                ProgrammabilityRecord::Trigger(t) => Some(t),
                _ => None,
            }),
            database_id,
            container_id,
        )
        .into_iter()
        .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        trigger: Trigger,
    ) -> CosmosResult<Trigger> {
        let existing = self
            .get_trigger(database_id, container_id, &trigger.id)
            .await?;
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
        let key = make_record_key(database_id, container_id, &updated.id);
        self.record_store
            .upsert_record(
                ProgrammabilityTable::Triggers,
                &key,
                ProgrammabilityRecord::Trigger(updated.clone()),
            )
            .await?;
        Ok(updated)
    }

    async fn delete_trigger(
        &self,
        database_id: &str,
        container_id: &str,
        trigger_id: &str,
    ) -> CosmosResult<()> {
        let key = make_record_key(database_id, container_id, trigger_id);
        self.record_store
            .delete_record(ProgrammabilityTable::Triggers, &key, "Trigger", trigger_id)
            .await
    }

    async fn create_udf(
        &self,
        database_id: &str,
        container_id: &str,
        mut udf: UserDefinedFunction,
    ) -> CosmosResult<UserDefinedFunction> {
        let key = make_record_key(database_id, container_id, &udf.id);
        if self
            .record_store
            .select_record(ProgrammabilityTable::UserDefinedFunctions, &key)
            .await?
            .is_some()
        {
            return Err(CosmosError::conflict("UserDefinedFunction", &udf.id));
        }
        udf.database_id = database_id.to_string();
        udf.container_id = container_id.to_string();
        udf.self_link = format!("dbs/{database_id}/colls/{container_id}/udfs/{}/", udf.id);
        self.record_store
            .create_record(
                ProgrammabilityTable::UserDefinedFunctions,
                &key,
                ProgrammabilityRecord::UserDefinedFunction(udf.clone()),
            )
            .await?;
        Ok(udf)
    }

    async fn get_udf(
        &self,
        database_id: &str,
        container_id: &str,
        udf_id: &str,
    ) -> CosmosResult<UserDefinedFunction> {
        let key = make_record_key(database_id, container_id, udf_id);
        match self
            .record_store
            .select_record(ProgrammabilityTable::UserDefinedFunctions, &key)
            .await?
        {
            Some(ProgrammabilityRecord::UserDefinedFunction(u)) => Ok(u),
            _ => Err(CosmosError::not_found("UserDefinedFunction", udf_id)),
        }
    }

    async fn list_udfs(
        &self,
        database_id: &str,
        container_id: &str,
    ) -> CosmosResult<FeedResponse<UserDefinedFunction>> {
        let records = self
            .record_store
            .select_table_records(ProgrammabilityTable::UserDefinedFunctions)
            .await?;
        let items = Self::sorted(
            records.into_iter().filter_map(|record| match record {
                ProgrammabilityRecord::UserDefinedFunction(u) => Some(u),
                _ => None,
            }),
            database_id,
            container_id,
        )
        .into_iter()
        .collect();
        Ok(FeedResponse::new(items))
    }

    async fn replace_udf(
        &self,
        database_id: &str,
        container_id: &str,
        udf: UserDefinedFunction,
    ) -> CosmosResult<UserDefinedFunction> {
        let existing = self.get_udf(database_id, container_id, &udf.id).await?;
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
        let key = make_record_key(database_id, container_id, &updated.id);
        self.record_store
            .upsert_record(
                ProgrammabilityTable::UserDefinedFunctions,
                &key,
                ProgrammabilityRecord::UserDefinedFunction(updated.clone()),
            )
            .await?;
        Ok(updated)
    }

    async fn delete_udf(
        &self,
        database_id: &str,
        container_id: &str,
        udf_id: &str,
    ) -> CosmosResult<()> {
        let key = make_record_key(database_id, container_id, udf_id);
        self.record_store
            .delete_record(
                ProgrammabilityTable::UserDefinedFunctions,
                &key,
                "UserDefinedFunction",
                udf_id,
            )
            .await
    }
}
