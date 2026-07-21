//! Resource-ID and ETag generation. Ports `ResourceId` / `ETagGenerator`
//! from `src/Core/Models/ResourceId.cs`.

use std::sync::atomic::{AtomicI64, Ordering};

use base64::Engine;

static COUNTER: AtomicI64 = AtomicI64::new(0);

/// Generates a short base64 resource ID similar to Cosmos DB `_rid` values.
///
/// The counter is seeded lazily from the current Unix-millis so IDs stay
/// monotonic and unique across process restarts, matching the .NET behaviour.
pub fn resource_id() -> String {
    // Seed on first use from the wall clock.
    let _ = COUNTER.compare_exchange(
        0,
        chrono::Utc::now().timestamp_millis(),
        Ordering::SeqCst,
        Ordering::SeqCst,
    );
    let value = COUNTER.fetch_add(1, Ordering::SeqCst) + 1;
    base64::engine::general_purpose::STANDARD.encode(value.to_le_bytes())
}

/// Generates a quoted ETag value in the Cosmos DB format, e.g. `"a1b2c3d4e5f60718"`.
pub fn etag() -> String {
    let bytes: [u8; 8] = rand_bytes();
    let hex: String = bytes.iter().map(|b| format!("{b:02x}")).collect();
    format!("\"{hex}\"")
}

/// Returns 8 random bytes without pulling in the `rand` crate, using the
/// nanosecond clock and an atomic salt as an entropy source (sufficient for
/// non-cryptographic ETags).
fn rand_bytes() -> [u8; 8] {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);
    let salt = COUNTER.fetch_add(1, Ordering::Relaxed) as u64;
    (nanos ^ salt.rotate_left(32)).to_le_bytes()
}
