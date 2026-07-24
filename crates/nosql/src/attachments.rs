//! Document attachment endpoints. Ports `AttachmentsController`.
//!
//! Attachments are deprecated in Azure Cosmos DB; the .NET controller returns
//! HTTP 410 for every collection and item operation.

use axum::extract::Path;
use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use serde_json::json;

const DEPRECATION_MESSAGE: &str = "Attachments are deprecated in Azure Cosmos DB and are not supported by this emulator. Use Azure Blob Storage for binary data. See: https://learn.microsoft.com/en-us/azure/cosmos-db/attachments";

pub async fn handle_collection(
    Path((_db_id, _coll_id, _doc_id)): Path<(String, String, String)>,
) -> Response {
    gone_response()
}

pub async fn handle_item(
    Path((_db_id, _coll_id, _doc_id, _attachment_id)): Path<(String, String, String, String)>,
) -> Response {
    gone_response()
}

fn gone_response() -> Response {
    (
        StatusCode::GONE,
        axum::Json(json!({
            "code": "Gone",
            "message": DEPRECATION_MESSAGE,
        })),
    )
        .into_response()
}
