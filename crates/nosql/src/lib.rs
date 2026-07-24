//! Cosmos DB NoSQL REST API. Ports the .NET `NoSql` project's controllers and
//! middleware to Axum routers and tower layers.
//!
//! URL structure mirrors the Cosmos REST API:
//! `/dbs`, `/dbs/{dbId}/colls`, `/dbs/{dbId}/colls/{collId}/docs`, `/offers`, etc.
//!
//! Middleware order (parity with .NET): exception mapping (via [`response::ApiError`]
//! `IntoResponse`) → auth ([`auth_mw`]) → handlers. The stored-procedure/trigger/UDF
//! programmability surface and the full SQL query engine are ported in the
//! `triggers-crate` and `query-crate` phases respectively.

mod addresses;
mod attachments;
mod auth_mw;
mod batch;
mod changefeed;
mod consistency_mw;
mod containers;
mod databases;
mod documents;
mod emulator_info;
mod format;
mod offers;
mod parse;
mod permissions;
mod pkranges;
mod programmability;
mod response;
mod ru;
mod state;
mod users;

pub use response::ApiError;
pub use state::{AppState, RuntimeState};

use axum::routing::{get, post};
use axum::Router;

/// Builds the NoSQL REST router with all controllers wired and the auth layer
/// applied (a no-op when `state.auth` is `None`).
pub fn router(state: AppState) -> Router {
    let api = Router::new()
        // Address resolution
        .route("/addresses", get(addresses::get))
        // Emulator admin/info endpoints
        .route("/api/emulator/info", get(emulator_info::info))
        .route("/api/emulator/stats", get(emulator_info::stats))
        .route("/api/emulator/explain", post(emulator_info::explain))
        .route(
            "/api/emulator/settings",
            axum::routing::put(emulator_info::update_settings),
        )
        .route(
            "/api/emulator/throughput/database/:dbId",
            get(emulator_info::get_database_throughput)
                .put(emulator_info::update_database_throughput),
        )
        .route(
            "/api/emulator/throughput/container/:dbId/:collId",
            get(emulator_info::get_container_throughput)
                .put(emulator_info::update_container_throughput),
        )
        // Databases
        .route("/dbs", post(databases::create).get(databases::list))
        .route("/dbs/:dbId", get(databases::get).delete(databases::delete))
        // Containers (+ transactional batch on the container path via POST)
        .route(
            "/dbs/:dbId/colls",
            post(containers::create).get(containers::list),
        )
        .route(
            "/dbs/:dbId/colls/:collId",
            get(containers::get)
                .put(containers::replace)
                .delete(containers::delete)
                .post(batch::execute),
        )
        // Partition key ranges
        .route("/dbs/:dbId/colls/:collId/pkranges", get(pkranges::list))
        // Stored procedures / triggers / UDFs
        .route(
            "/dbs/:dbId/colls/:collId/sprocs",
            post(programmability::create_sproc).get(programmability::list_sprocs),
        )
        .route(
            "/dbs/:dbId/colls/:collId/sprocs/:sprocId",
            get(programmability::get_sproc)
                .post(programmability::execute_sproc)
                .put(programmability::replace_sproc)
                .delete(programmability::delete_sproc),
        )
        .route(
            "/dbs/:dbId/colls/:collId/triggers",
            post(programmability::create_trigger).get(programmability::list_triggers),
        )
        .route(
            "/dbs/:dbId/colls/:collId/triggers/:triggerId",
            get(programmability::get_trigger)
                .put(programmability::replace_trigger)
                .delete(programmability::delete_trigger),
        )
        .route(
            "/dbs/:dbId/colls/:collId/udfs",
            post(programmability::create_udf).get(programmability::list_udfs),
        )
        .route(
            "/dbs/:dbId/colls/:collId/udfs/:udfId",
            get(programmability::get_udf)
                .put(programmability::replace_udf)
                .delete(programmability::delete_udf),
        )
        // Documents
        .route(
            "/dbs/:dbId/colls/:collId/docs",
            post(documents::create_or_query).delete(documents::delete_all),
        )
        .route(
            "/dbs/:dbId/colls/:collId/docs/changefeed",
            get(changefeed::read),
        )
        .route(
            "/dbs/:dbId/colls/:collId/docs/:docId",
            get(documents::read)
                .put(documents::replace)
                .delete(documents::delete)
                .patch(documents::patch),
        )
        // Attachments (deprecated)
        .route(
            "/dbs/:dbId/colls/:collId/docs/:docId/attachments",
            get(attachments::handle_collection).post(attachments::handle_collection),
        )
        .route(
            "/dbs/:dbId/colls/:collId/docs/:docId/attachments/:attachmentId",
            get(attachments::handle_item)
                .put(attachments::handle_item)
                .delete(attachments::handle_item),
        )
        // Users
        .route("/dbs/:dbId/users", post(users::create).get(users::list))
        .route(
            "/dbs/:dbId/users/:userId",
            get(users::get).put(users::replace).delete(users::delete),
        )
        // Permissions
        .route(
            "/dbs/:dbId/users/:userId/permissions",
            post(permissions::create).get(permissions::list),
        )
        .route(
            "/dbs/:dbId/users/:userId/permissions/:permissionId",
            get(permissions::get)
                .put(permissions::replace)
                .delete(permissions::delete),
        )
        // Offers
        .route("/offers", get(offers::list).post(offers::query))
        .route("/offers/:offerId", get(offers::get).put(offers::replace));

    api.layer(axum::middleware::from_fn_with_state(
        state.clone(),
        consistency_mw::validate,
    ))
    .layer(axum::middleware::from_fn_with_state(
        state.clone(),
        auth_mw::authenticate,
    ))
    .with_state(state)
}
