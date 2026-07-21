//! MongoDB wire-protocol message framing and (de)serialization.
//!
//! Ports the framing logic in `MongoDbConnectionHandler`, using the `bson`
//! crate for correct document (de)serialization (the .NET stub wrapped raw
//! JSON bytes as pseudo-BSON). Supports the message header, `OP_MSG` (2013),
//! `OP_QUERY` (2004, legacy handshake) and `OP_REPLY` (1) op-codes.

use std::io;

use bson::Document;

/// `OP_MSG` op-code.
pub const OP_MSG: i32 = 2013;
/// Legacy `OP_QUERY` op-code (used by older drivers for the initial handshake).
pub const OP_QUERY: i32 = 2004;
/// Legacy `OP_REPLY` op-code (response to `OP_QUERY`).
pub const OP_REPLY: i32 = 1;

/// The 16-byte MongoDB message header.
#[derive(Debug, Clone, Copy)]
pub struct MsgHeader {
    pub message_length: i32,
    pub request_id: i32,
    pub response_to: i32,
    pub op_code: i32,
}

impl MsgHeader {
    pub const SIZE: usize = 16;

    pub fn parse(buf: &[u8; 16]) -> Self {
        Self {
            message_length: i32::from_le_bytes(buf[0..4].try_into().unwrap()),
            request_id: i32::from_le_bytes(buf[4..8].try_into().unwrap()),
            response_to: i32::from_le_bytes(buf[8..12].try_into().unwrap()),
            op_code: i32::from_le_bytes(buf[12..16].try_into().unwrap()),
        }
    }
}

fn read_i32(buf: &[u8], offset: usize) -> io::Result<i32> {
    buf.get(offset..offset + 4)
        .map(|s| i32::from_le_bytes(s.try_into().unwrap()))
        .ok_or_else(|| io::Error::new(io::ErrorKind::UnexpectedEof, "truncated i32"))
}

/// Reads a BSON document at `offset`, returning the document and the number of
/// bytes it occupied.
fn read_document(buf: &[u8], offset: usize) -> io::Result<(Document, usize)> {
    let len = read_i32(buf, offset)? as usize;
    if len < 5 || offset + len > buf.len() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid BSON document length",
        ));
    }
    let doc = Document::from_reader(&buf[offset..offset + len])
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, format!("BSON parse: {e}")))?;
    Ok((doc, len))
}

/// A parsed request command plus the `request_id` to echo back in `response_to`.
#[derive(Debug)]
pub struct ParsedCommand {
    pub request_id: i32,
    pub op_code: i32,
    /// The command document (OP_MSG body section, or OP_QUERY query document).
    pub command: Document,
}

/// Parses an `OP_MSG` body (flags + sections). Only section kind 0 (body) and
/// kind 1 (document sequence) are recognised; kind 0 carries the command.
pub fn parse_op_msg(request_id: i32, body: &[u8]) -> io::Result<ParsedCommand> {
    // 4-byte flagBits, then sections.
    let mut offset = 4;
    let mut command: Option<Document> = None;
    let mut sequences: Vec<(String, Vec<Document>)> = Vec::new();

    while offset < body.len() {
        let kind = body[offset];
        offset += 1;
        match kind {
            0 => {
                let (doc, len) = read_document(body, offset)?;
                offset += len;
                command = Some(doc);
            }
            1 => {
                // Document sequence: int32 size (incl. itself) + cstring identifier + documents.
                let seq_size = read_i32(body, offset)? as usize;
                let seq_end = offset + seq_size;
                if seq_end > body.len() {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidData,
                        "document sequence overruns body",
                    ));
                }
                let mut inner = offset + 4;
                // identifier cstring
                let id_start = inner;
                while inner < seq_end && body[inner] != 0 {
                    inner += 1;
                }
                let identifier = String::from_utf8_lossy(&body[id_start..inner]).into_owned();
                inner += 1; // skip NUL
                let mut docs = Vec::new();
                while inner < seq_end {
                    let (doc, len) = read_document(body, inner)?;
                    inner += len;
                    docs.push(doc);
                }
                sequences.push((identifier, docs));
                offset = seq_end;
            }
            other => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    format!("unknown OP_MSG section kind {other}"),
                ));
            }
        }
    }

    let mut command = command
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "OP_MSG missing body section"))?;

    // Fold any document sequences back into the command document as arrays, the
    // way a driver splits e.g. `documents`/`updates`/`deletes` out of insert/
    // update/delete commands.
    for (identifier, docs) in sequences {
        let arr = docs.into_iter().map(bson::Bson::Document).collect();
        command.insert(identifier, bson::Bson::Array(arr));
    }

    Ok(ParsedCommand {
        request_id,
        op_code: OP_MSG,
        command,
    })
}

/// Parses a legacy `OP_QUERY` body, extracting the query document (used for the
/// `isMaster`/`hello` handshake by older drivers).
pub fn parse_op_query(request_id: i32, body: &[u8]) -> io::Result<ParsedCommand> {
    // int32 flags, cstring fullCollectionName, int32 numberToSkip,
    // int32 numberToReturn, document query.
    let mut offset = 4;
    while offset < body.len() && body[offset] != 0 {
        offset += 1;
    }
    offset += 1; // NUL
    offset += 8; // numberToSkip + numberToReturn
    let (command, _) = read_document(body, offset)?;
    Ok(ParsedCommand {
        request_id,
        op_code: OP_QUERY,
        command,
    })
}

fn write_header(out: &mut Vec<u8>, response_to: i32, op_code: i32) {
    out.extend_from_slice(&0i32.to_le_bytes()); // messageLength placeholder
    out.extend_from_slice(&0i32.to_le_bytes()); // requestId
    out.extend_from_slice(&response_to.to_le_bytes());
    out.extend_from_slice(&op_code.to_le_bytes());
}

fn finalize_length(out: &mut [u8]) {
    let len = out.len() as i32;
    out[0..4].copy_from_slice(&len.to_le_bytes());
}

/// Encodes an `OP_MSG` response carrying a single body document.
pub fn encode_op_msg(response_to: i32, doc: &Document) -> Vec<u8> {
    let mut out = Vec::with_capacity(64);
    write_header(&mut out, response_to, OP_MSG);
    out.extend_from_slice(&0u32.to_le_bytes()); // flagBits
    out.push(0u8); // section kind 0 = body
    let mut buf = Vec::new();
    doc.to_writer(&mut buf).expect("BSON serialize");
    out.extend_from_slice(&buf);
    finalize_length(&mut out);
    out
}

/// Encodes a legacy `OP_REPLY` response carrying a single document.
pub fn encode_op_reply(response_to: i32, doc: &Document) -> Vec<u8> {
    let mut out = Vec::with_capacity(64);
    write_header(&mut out, response_to, OP_REPLY);
    out.extend_from_slice(&0i32.to_le_bytes()); // responseFlags
    out.extend_from_slice(&0i64.to_le_bytes()); // cursorId
    out.extend_from_slice(&0i32.to_le_bytes()); // startingFrom
    out.extend_from_slice(&1i32.to_le_bytes()); // numberReturned
    let mut buf = Vec::new();
    doc.to_writer(&mut buf).expect("BSON serialize");
    out.extend_from_slice(&buf);
    finalize_length(&mut out);
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use bson::doc;

    #[test]
    fn op_msg_round_trip() {
        let cmd = doc! { "hello": 1, "$db": "admin" };
        let encoded = encode_op_msg(7, &cmd);
        // Strip the 16-byte header and reparse the body.
        let header = {
            let mut h = [0u8; 16];
            h.copy_from_slice(&encoded[0..16]);
            MsgHeader::parse(&h)
        };
        assert_eq!(header.op_code, OP_MSG);
        assert_eq!(header.response_to, 7);
        assert_eq!(header.message_length as usize, encoded.len());
        let parsed = parse_op_msg(1, &encoded[16..]).unwrap();
        assert_eq!(parsed.command.get_i32("hello").unwrap(), 1);
    }

    #[test]
    fn op_msg_document_sequence_folds_into_command() {
        // Build an OP_MSG body: flags + kind0(body) + kind1(documents seq).
        let mut body = Vec::new();
        body.extend_from_slice(&0u32.to_le_bytes());
        // kind 0 body
        body.push(0);
        let mut buf = Vec::new();
        doc! { "insert": "c", "$db": "d" }
            .to_writer(&mut buf)
            .unwrap();
        body.extend_from_slice(&buf);
        // kind 1 sequence "documents"
        body.push(1);
        let mut seq = Vec::new();
        let identifier = b"documents\0";
        let mut docs_bytes = Vec::new();
        for d in [doc! {"id": "a"}, doc! {"id": "b"}] {
            let mut b = Vec::new();
            d.to_writer(&mut b).unwrap();
            docs_bytes.extend_from_slice(&b);
        }
        let seq_size = (4 + identifier.len() + docs_bytes.len()) as i32;
        seq.extend_from_slice(&seq_size.to_le_bytes());
        seq.extend_from_slice(identifier);
        seq.extend_from_slice(&docs_bytes);
        body.extend_from_slice(&seq);

        let parsed = parse_op_msg(1, &body).unwrap();
        assert_eq!(parsed.command.get_str("insert").unwrap(), "c");
        let arr = parsed.command.get_array("documents").unwrap();
        assert_eq!(arr.len(), 2);
    }
}
