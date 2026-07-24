//! Embedded Explorer SPA. Mirrors the .NET host, which embeds the built
//! `wwwroot/explorer/` assets into the assembly via `ManifestEmbeddedFileProvider`
//! (Release) and serves them under `/explorer` with an SPA fallback to
//! `index.html`.
//!
//! The assets are produced by the Vite build (`explorer/` → `vite build`,
//! output `crates/explorer/wwwroot/explorer/`). In debug builds `rust-embed`
//! reads them from disk at runtime; in release builds they are embedded into the
//! binary, so no `--explorer-dir` is required.
//!
//! This crate is pulled into the host only when the host's `explorer` feature is
//! enabled (the default), so a slim emulator binary can be produced by building
//! with `--no-default-features`.

use axum::body::Body;
use axum::extract::Path;
use axum::http::{header, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use axum::Router;
use rust_embed::RustEmbed;

#[derive(RustEmbed)]
#[folder = "wwwroot/explorer"]
struct ExplorerAssets;

/// Returns `true` when the Explorer SPA assets were embedded (i.e. the build
/// output existed at compile time). Used to advertise the endpoint only when
/// it can actually be served.
pub fn is_available() -> bool {
    ExplorerAssets::get("index.html").is_some()
}

/// Builds the `/explorer` router serving the embedded SPA with a fallback to
/// `index.html` for client-side routes (non-file paths).
pub fn router() -> Router {
    Router::new()
        .route("/explorer", get(|| async { index_html() }))
        .route("/explorer/", get(|| async { index_html() }))
        .route(
            "/explorer/*path",
            get(|Path(path): Path<String>| async move { serve(&path) }),
        )
}

fn index_html() -> Response {
    serve("index.html")
}

fn serve(path: &str) -> Response {
    let path = path.trim_start_matches('/');
    match ExplorerAssets::get(path) {
        Some(content) => {
            let mime = mime_guess::from_path(path).first_or_octet_stream();
            let mut resp = Response::new(Body::from(content.data.into_owned()));
            resp.headers_mut().insert(
                header::CONTENT_TYPE,
                HeaderValue::from_str(mime.as_ref())
                    .unwrap_or_else(|_| HeaderValue::from_static("application/octet-stream")),
            );
            resp
        }
        // SPA fallback: unknown non-file paths return index.html so the React
        // router can handle them (matches the .NET `MapFallback`).
        None => match ExplorerAssets::get("index.html") {
            Some(content) => {
                let mut resp = Response::new(Body::from(content.data.into_owned()));
                resp.headers_mut()
                    .insert(header::CONTENT_TYPE, HeaderValue::from_static("text/html"));
                resp
            }
            None => (StatusCode::NOT_FOUND, "Explorer not built").into_response(),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn assets_are_embedded() {
        assert!(is_available(), "Explorer index.html should be embedded");
    }
}
