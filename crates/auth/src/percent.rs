//! Minimal percent-encoding codec, mirroring .NET's `Uri.EscapeDataString` /
//! `Uri.UnescapeDataString` for the subset needed to parse and generate Cosmos
//! authorization headers (which are fully percent-encoded key/value strings).

/// Percent-decodes `input` (equivalent to `Uri.UnescapeDataString`).
///
/// Invalid or truncated `%XX` sequences are passed through verbatim, matching
/// the lenient behaviour callers rely on for already-decoded headers.
pub fn decode(input: &str) -> String {
    let bytes = input.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'%' if i + 2 < bytes.len() => match (hex_val(bytes[i + 1]), hex_val(bytes[i + 2])) {
                (Some(h), Some(l)) => {
                    out.push((h << 4) | l);
                    i += 3;
                }
                _ => {
                    out.push(bytes[i]);
                    i += 1;
                }
            },
            b => {
                out.push(b);
                i += 1;
            }
        }
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// Percent-encodes `input` (equivalent to `Uri.EscapeDataString`): every byte
/// that is not an RFC 3986 unreserved character (`A-Z a-z 0-9 - _ . ~`) is
/// escaped as `%XX`.
pub fn encode(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    for &b in input.as_bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            out.push(b as char);
        } else {
            out.push('%');
            out.push(hex_digit(b >> 4));
            out.push(hex_digit(b & 0x0f));
        }
    }
    out
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

fn hex_digit(v: u8) -> char {
    match v {
        0..=9 => (b'0' + v) as char,
        _ => (b'A' + (v - 10)) as char,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_reserved_characters() {
        let raw = "type=master&ver=1.0&sig=ab+cd/ef==";
        let encoded = encode(raw);
        assert!(!encoded.contains('='));
        assert!(!encoded.contains('&'));
        assert!(!encoded.contains('+'));
        assert_eq!(decode(&encoded), raw);
    }

    #[test]
    fn passes_through_invalid_escape() {
        assert_eq!(decode("100%"), "100%");
        assert_eq!(decode("a%zz"), "a%zz");
    }
}
