//! Authentication for the Cosmos DB light emulator. Ports the .NET `Auth` project.
//!
//! Providers: master key (HMAC-SHA256), EntraID (JWT), resource tokens, chained
//! by a composite provider. All implement [`cosmos_core::traits::AuthProvider`].
//!
//! ## Signature parity (critical)
//! The HMAC payload is `"{verb}\n{resourceType}\n{resourceLink}\n{date}\n\n"`,
//! all lowercased **except** `resourceLink`, which is case-sensitive and must
//! preserve its original casing. Lowercasing it is a classic parity regression.

use base64::Engine;
use hmac::{Hmac, Mac};
use sha2::Sha256;

pub mod composite;
pub mod entra_id;
pub mod master_key;
mod percent;
pub mod resource_token;

pub use composite::CompositeAuthProvider;
pub use entra_id::EntraIdAuthProvider;
pub use master_key::MasterKeyAuthProvider;
pub use resource_token::{
    generate_token, normalize_resource_link, parse_token, ResourcePermission, ResourceToken,
    ResourceTokenProvider,
};

type HmacSha256 = Hmac<Sha256>;

/// The well-known default master key used by the Azure Cosmos DB Emulator.
pub const DEFAULT_MASTER_KEY: &str =
    "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

/// Computes the Cosmos master-key authorization signature.
///
/// `resource_type` is lowercased; `resource_link` MUST be passed with its
/// original casing (name-based links are case-sensitive).
pub fn master_key_signature(
    master_key_base64: &str,
    verb: &str,
    resource_type: &str,
    resource_link: &str,
    date: &str,
) -> Result<String, anyhow::Error> {
    let key = base64::engine::general_purpose::STANDARD.decode(master_key_base64)?;
    let payload = format!(
        "{}\n{}\n{}\n{}\n\n",
        verb.to_lowercase(),
        resource_type.to_lowercase(),
        resource_link, // case-sensitive — do NOT lowercase
        date.to_lowercase(),
    );
    let mut mac = HmacSha256::new_from_slice(&key)?;
    mac.update(payload.as_bytes());
    let sig = mac.finalize().into_bytes();
    Ok(base64::engine::general_purpose::STANDARD.encode(sig))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn signature_is_deterministic_and_case_sensitive_on_link() {
        let a = master_key_signature(DEFAULT_MASTER_KEY, "GET", "dbs", "dbs/MyDb", "x").unwrap();
        let b = master_key_signature(DEFAULT_MASTER_KEY, "GET", "dbs", "dbs/mydb", "x").unwrap();
        // Different link casing must yield different signatures.
        assert_ne!(a, b);
        // Deterministic for identical inputs.
        let a2 = master_key_signature(DEFAULT_MASTER_KEY, "GET", "dbs", "dbs/MyDb", "x").unwrap();
        assert_eq!(a, a2);
    }
}
