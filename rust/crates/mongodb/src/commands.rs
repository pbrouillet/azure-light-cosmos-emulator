//! Command dispatch for the MongoDB wire-protocol server.
//!
//! The .NET emulator's handler is a scaffold: it answers the `isMaster`/`hello`
//! handshake and returns `{ ok: 1 }` for every `OP_MSG` command (document CRUD
//! against the store is a documented TODO). This port mirrors that scope but
//! recognises the standard handshake/diagnostic commands (`hello`, `isMaster`,
//! `ping`, `buildInfo`, `getLog`, `whatsmyuri`, `endSessions`) so a real driver
//! can complete its connection handshake.

use bson::{doc, Document};

/// Max BSON document size advertised in the handshake (16 MiB).
pub const MAX_BSON_OBJECT_SIZE: i32 = 16 * 1024 * 1024;
/// Max wire message size advertised in the handshake (48 MB).
pub const MAX_MESSAGE_SIZE_BYTES: i32 = 48_000_000;
/// Max documents per write batch advertised in the handshake.
pub const MAX_WRITE_BATCH_SIZE: i32 = 100_000;
/// Emulated server version reported by `buildInfo`/`hello`.
pub const SERVER_VERSION: &str = "5.0.0";

/// Returns the command name (first key of the command document), lowercased for
/// matching. MongoDB command names are case-sensitive on the wire but the
/// handshake aliases (`isMaster`/`ismaster`/`hello`) vary by driver, so we match
/// case-insensitively.
fn command_name(command: &Document) -> Option<String> {
    command.keys().next().map(|k| k.to_ascii_lowercase())
}

/// Builds the `hello`/`isMaster` handshake response document.
pub fn hello_response() -> Document {
    doc! {
        "isWritablePrimary": true,
        "ismaster": true,
        "helloOk": true,
        "maxBsonObjectSize": MAX_BSON_OBJECT_SIZE,
        "maxMessageSizeBytes": MAX_MESSAGE_SIZE_BYTES,
        "maxWriteBatchSize": MAX_WRITE_BATCH_SIZE,
        "localTime": bson::DateTime::now(),
        "logicalSessionTimeoutMinutes": 30i32,
        "connectionId": 1i32,
        "minWireVersion": 0i32,
        "maxWireVersion": 17i32,
        "readOnly": false,
        "ok": 1.0,
    }
}

fn build_info_response() -> Document {
    let parts: Vec<bson::Bson> = SERVER_VERSION
        .split('.')
        .map(|p| bson::Bson::Int32(p.parse().unwrap_or(0)))
        .collect();
    doc! {
        "version": SERVER_VERSION,
        "gitVersion": "0000000000000000000000000000000000000000",
        "versionArray": parts,
        "maxBsonObjectSize": MAX_BSON_OBJECT_SIZE,
        "ok": 1.0,
    }
}

/// Dispatches a parsed command document to a response document. Unknown
/// commands return `{ ok: 1 }`, matching the .NET stub's permissive behaviour.
pub fn dispatch(command: &Document) -> Document {
    match command_name(command).as_deref() {
        Some("hello") | Some("ismaster") => hello_response(),
        Some("ping") => doc! { "ok": 1.0 },
        Some("buildinfo") => build_info_response(),
        Some("getlog") => {
            doc! { "totalLinesWritten": 0i32, "log": bson::Bson::Array(vec![]), "ok": 1.0 }
        }
        Some("whatsmyuri") => doc! { "you": "127.0.0.1:0", "ok": 1.0 },
        Some("endsessions") => doc! { "ok": 1.0 },
        Some("getparameter") => doc! { "ok": 1.0 },
        _ => doc! { "ok": 1.0 },
    }
}

/// Builds an error response document (`{ ok: 0, errmsg, code }`).
pub fn error_response(errmsg: &str, code: i32) -> Document {
    doc! { "ok": 0.0, "errmsg": errmsg, "code": code, "codeName": "CommandFailed" }
}

#[cfg(test)]
mod tests {
    use super::*;
    use bson::doc;

    #[test]
    fn hello_is_writable_primary() {
        let r = dispatch(&doc! { "hello": 1 });
        assert!(r.get_bool("isWritablePrimary").unwrap());
        assert_eq!(r.get_f64("ok").unwrap(), 1.0);
    }

    #[test]
    fn ismaster_alias() {
        let r = dispatch(&doc! { "isMaster": 1 });
        assert!(r.get_bool("ismaster").unwrap());
    }

    #[test]
    fn ping_ok() {
        let r = dispatch(&doc! { "ping": 1 });
        assert_eq!(r.get_f64("ok").unwrap(), 1.0);
    }

    #[test]
    fn build_info_version() {
        let r = dispatch(&doc! { "buildInfo": 1 });
        assert_eq!(r.get_str("version").unwrap(), SERVER_VERSION);
        assert_eq!(r.get_array("versionArray").unwrap().len(), 3);
    }

    #[test]
    fn unknown_command_is_ok() {
        let r = dispatch(&doc! { "someRandomCommand": 1 });
        assert_eq!(r.get_f64("ok").unwrap(), 1.0);
    }
}
