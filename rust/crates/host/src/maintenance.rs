use std::sync::Arc;
use std::time::Duration;

use cosmos_core::consistency::ConsistencyManager;
use cosmos_core::models::{CosmosContainer, CosmosDocument};
use cosmos_core::traits::{ConsistencyManager as _, DocumentStore};
use cosmos_core::ConsistencyLevel;

const TTL_CLEANUP_INTERVAL: Duration = Duration::from_secs(30);
const DATA_MAINTENANCE_INTERVAL: Duration = Duration::from_secs(300);

pub fn spawn(store: Arc<dyn DocumentStore>, consistency: ConsistencyLevel) {
    let cleanup_store = store.clone();
    let consistency = Arc::new(ConsistencyManager::new(consistency));
    tokio::spawn(async move {
        run_ttl_cleanup_loop(cleanup_store, consistency).await;
    });

    tokio::spawn(async move {
        run_data_maintenance_loop(store).await;
    });
}

async fn run_ttl_cleanup_loop(store: Arc<dyn DocumentStore>, consistency: Arc<ConsistencyManager>) {
    run_ttl_cleanup(&store, &consistency).await;
    loop {
        tokio::time::sleep(TTL_CLEANUP_INTERVAL).await;
        run_ttl_cleanup(&store, &consistency).await;
    }
}

async fn run_ttl_cleanup(store: &Arc<dyn DocumentStore>, consistency: &ConsistencyManager) {
    if let Err(error) = cleanup_expired_documents(store, consistency).await {
        tracing::error!(%error, "TTL cleanup iteration failed");
    }
}

async fn cleanup_expired_documents(
    store: &Arc<dyn DocumentStore>,
    consistency: &ConsistencyManager,
) -> Result<(), anyhow::Error> {
    let now = chrono::Utc::now().timestamp();
    let databases = store.list_databases().await?;
    for database in databases.resources {
        let containers = store.list_containers(&database.id).await?;
        for container in containers.resources.into_iter().filter(has_enabled_ttl) {
            let documents = store.list_documents(&database.id, &container.id).await?;
            for document in documents.resources {
                let Some(ttl) = resolve_effective_ttl(&document, &container) else {
                    continue;
                };
                if ttl <= 0 || document.timestamp + i64::from(ttl) >= now {
                    continue;
                }

                store
                    .delete_document(
                        &database.id,
                        &container.id,
                        &document.id,
                        &document.partition_key,
                    )
                    .await?;
                let lsn = store.get_global_lsn().await.unwrap_or(document.lsn);
                consistency.generate_session_token(&database.id, &container.id, lsn);
                tracing::info!(
                    database_id = %database.id,
                    container_id = %container.id,
                    document_id = %document.id,
                    "deleted expired document via TTL cleanup"
                );
            }
        }
    }
    Ok(())
}

async fn run_data_maintenance_loop(store: Arc<dyn DocumentStore>) {
    run_data_maintenance(&store).await;
    loop {
        tokio::time::sleep(DATA_MAINTENANCE_INTERVAL).await;
        run_data_maintenance(&store).await;
    }
}

async fn run_data_maintenance(store: &Arc<dyn DocumentStore>) {
    match store.get_global_lsn().await {
        Ok(lsn) => tracing::debug!(lsn, "data maintenance completed"),
        Err(error) => tracing::debug!(%error, "data maintenance skipped"),
    }
}

fn has_enabled_ttl(container: &CosmosContainer) -> bool {
    container.default_time_to_live.is_some_and(|ttl| ttl > 0)
}

fn resolve_effective_ttl(document: &CosmosDocument, container: &CosmosContainer) -> Option<i32> {
    match document.time_to_live {
        Some(ttl) if ttl > 0 => Some(ttl),
        Some(-1) => None,
        Some(0) | None => container.default_time_to_live,
        _ => None,
    }
}
