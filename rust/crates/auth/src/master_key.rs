//! Master-key HMAC-SHA256 authorization. Ports `MasterKeyAuthProvider`.

use async_trait::async_trait;
use base64::Engine;
use cosmos_core::traits::{AuthProvider, AuthResult, AuthType};

use crate::percent;
use crate::{master_key_signature, DEFAULT_MASTER_KEY};

/// Validates Cosmos DB master-key `type=master&ver=1.0&sig=...` headers.
pub struct MasterKeyAuthProvider {
    master_key: String,
}

impl MasterKeyAuthProvider {
    pub fn new(master_key: impl Into<String>) -> Self {
        Self {
            master_key: master_key.into(),
        }
    }

    /// Computes the HMAC-SHA256 signature for a request (see
    /// [`master_key_signature`]).
    pub fn compute_signature(
        &self,
        verb: &str,
        resource_type: &str,
        resource_link: &str,
        date: &str,
    ) -> Result<String, anyhow::Error> {
        master_key_signature(&self.master_key, verb, resource_type, resource_link, date)
    }

    /// Generates a complete (percent-encoded) authorization header value.
    pub fn generate_auth_header(
        &self,
        verb: &str,
        resource_type: &str,
        resource_link: &str,
        date: &str,
    ) -> Result<String, anyhow::Error> {
        let sig = self.compute_signature(verb, resource_type, resource_link, date)?;
        Ok(percent::encode(&format!("type=master&ver=1.0&sig={sig}")))
    }

    /// Generates a new random 64-byte master key (base64-encoded).
    pub fn generate_master_key() -> String {
        // A non-cryptographic but sufficiently unique seed source is avoided in
        // favour of `getrandom` via `uuid`; two v4 UUIDs give 256 bits.
        let mut bytes = Vec::with_capacity(64);
        for _ in 0..4 {
            bytes.extend_from_slice(uuid_bytes().as_slice());
        }
        base64::engine::general_purpose::STANDARD.encode(&bytes)
    }
}

impl Default for MasterKeyAuthProvider {
    fn default() -> Self {
        Self::new(DEFAULT_MASTER_KEY)
    }
}

#[async_trait]
impl AuthProvider for MasterKeyAuthProvider {
    async fn validate(
        &self,
        auth_header: &str,
        verb: &str,
        resource_type: &str,
        resource_link: &str,
        date_header: &str,
    ) -> AuthResult {
        if auth_header.is_empty() {
            return AuthResult::failure("Missing Authorization header.");
        }

        let (kind, version, signature) = match parse_auth_header(auth_header) {
            Some(parts) => parts,
            None => return AuthResult::failure("Invalid Authorization header format."),
        };

        if !kind.eq_ignore_ascii_case("master") {
            return AuthResult::failure(format!("Unsupported auth type: {kind}"));
        }
        if version != "1.0" {
            return AuthResult::failure(format!("Unsupported auth version: {version}"));
        }

        let expected = match self.compute_signature(verb, resource_type, resource_link, date_header)
        {
            Ok(sig) => sig,
            Err(e) => return AuthResult::failure(format!("Signature computation failed: {e}")),
        };

        if signature == expected {
            AuthResult::success(AuthType::MasterKey, None)
        } else {
            AuthResult::failure("Invalid master key signature.")
        }
    }
}

/// Parses `type={t}&ver={v}&sig={s}` from a (possibly percent-encoded) header.
pub(crate) fn parse_auth_header(header: &str) -> Option<(String, String, String)> {
    let decoded = percent::decode(header);
    let mut kind = String::new();
    let mut version = String::new();
    let mut signature = String::new();

    for part in decoded.split('&') {
        let (key, value) = match part.split_once('=') {
            Some(kv) => kv,
            None => continue,
        };
        match key.trim().to_ascii_lowercase().as_str() {
            "type" => kind = value.trim().to_string(),
            "ver" => version = value.trim().to_string(),
            "sig" => signature = value.trim().to_string(),
            _ => {}
        }
    }

    if kind.is_empty() || version.is_empty() || signature.is_empty() {
        None
    } else {
        Some((kind, version, signature))
    }
}

fn uuid_bytes() -> [u8; 16] {
    *uuid::Uuid::new_v4().as_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY: &str =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    #[tokio::test]
    async fn generated_header_validates() {
        let provider = MasterKeyAuthProvider::new(KEY);
        let header = provider
            .generate_auth_header("GET", "dbs", "dbs/MyDb", "sat, 21 jul 2026 13:00:00 gmt")
            .unwrap();
        let result = provider
            .validate(
                &header,
                "GET",
                "dbs",
                "dbs/MyDb",
                "sat, 21 jul 2026 13:00:00 gmt",
            )
            .await;
        assert!(result.is_authenticated);
        assert_eq!(result.auth_type, Some(AuthType::MasterKey));
    }

    #[tokio::test]
    async fn wrong_signature_is_rejected() {
        let provider = MasterKeyAuthProvider::new(KEY);
        let header = percent::encode("type=master&ver=1.0&sig=not-a-valid-signature");
        let result = provider
            .validate(&header, "GET", "dbs", "dbs/MyDb", "x")
            .await;
        assert!(!result.is_authenticated);
    }

    #[tokio::test]
    async fn missing_and_malformed_headers_fail() {
        let provider = MasterKeyAuthProvider::new(KEY);
        assert!(
            !provider
                .validate("", "GET", "dbs", "dbs/x", "x")
                .await
                .is_authenticated
        );
        let bad = percent::encode("type=resource&ver=1.0&sig=abc");
        assert!(
            !provider
                .validate(&bad, "GET", "dbs", "dbs/x", "x")
                .await
                .is_authenticated
        );
    }

    #[test]
    fn generate_master_key_is_base64_64_bytes() {
        let key = MasterKeyAuthProvider::generate_master_key();
        let decoded = base64::engine::general_purpose::STANDARD
            .decode(&key)
            .unwrap();
        assert_eq!(decoded.len(), 64);
    }
}
