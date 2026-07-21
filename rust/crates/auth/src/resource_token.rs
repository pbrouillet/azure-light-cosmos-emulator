//! Resource-token issuance and validation. Ports `ResourceTokenProvider`,
//! `ResourceTokenGenerator`, and `ResourceTokenModels`.
//!
//! A resource token is `base64(JSON{resourceLink, permissions, expiresAt,
//! signature})` where the signature is `HMAC-SHA256(masterKey,
//! "{resourceLink}\n{permissions}\n{expiresAt-rfc3339}")`. Generation and
//! validation both run in this port, so the signature payload only needs to be
//! self-consistent (byte-parity with the .NET `"O"` format is not required).

use async_trait::async_trait;
use base64::Engine;
use chrono::{DateTime, Duration, Utc};
use cosmos_core::traits::{AuthProvider, AuthResult, AuthType};
use hmac::{Hmac, Mac};
use serde::{Deserialize, Serialize};
use sha2::Sha256;

use crate::master_key::parse_auth_header;
use crate::percent;

type HmacSha256 = Hmac<Sha256>;

/// Permissions granted by a resource token. Ports `ResourcePermission`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResourcePermission {
    All,
    Read,
}

impl ResourcePermission {
    fn as_str(self) -> &'static str {
        match self {
            ResourcePermission::All => "All",
            ResourcePermission::Read => "Read",
        }
    }

    fn parse(value: &str) -> Option<Self> {
        match value.trim().to_ascii_lowercase().as_str() {
            "all" => Some(ResourcePermission::All),
            "read" => Some(ResourcePermission::Read),
            _ => None,
        }
    }
}

/// A parsed resource token. Ports the `ResourceToken` record.
#[derive(Debug, Clone)]
pub struct ResourceToken {
    pub resource_link: String,
    pub permissions: ResourcePermission,
    pub expires_at: DateTime<Utc>,
}

#[derive(Serialize, Deserialize)]
struct ResourceTokenPayload {
    #[serde(rename = "resourceLink")]
    resource_link: String,
    permissions: String,
    #[serde(rename = "expiresAt")]
    expires_at: DateTime<Utc>,
    signature: String,
}

/// Validates resource tokens against the master key. Ports `ResourceTokenProvider`.
pub struct ResourceTokenProvider {
    master_key: String,
}

impl ResourceTokenProvider {
    pub fn new(master_key: impl Into<String>) -> Self {
        let master_key = master_key.into();
        assert!(
            !master_key.trim().is_empty(),
            "master key must not be empty"
        );
        Self { master_key }
    }
}

#[async_trait]
impl AuthProvider for ResourceTokenProvider {
    async fn validate(
        &self,
        auth_header: &str,
        verb: &str,
        _resource_type: &str,
        resource_link: &str,
        _date_header: &str,
    ) -> AuthResult {
        let token = match extract_token(auth_header) {
            Ok(token) => token,
            Err(message) => return AuthResult::failure(message),
        };

        let parsed = match validate_token(&token, &self.master_key) {
            Ok(parsed) => parsed,
            Err(message) => return AuthResult::failure(message),
        };

        if parsed.expires_at <= Utc::now() {
            return AuthResult::failure("Resource token has expired.");
        }

        if !is_resource_link_match(&parsed.resource_link, resource_link) {
            return AuthResult::failure(
                "Resource token does not grant access to the requested resource.",
            );
        }

        if !is_permission_allowed(parsed.permissions, verb) {
            return AuthResult::failure(
                "Resource token does not grant permission for this operation.",
            );
        }

        AuthResult::success(AuthType::ResourceToken, None)
    }
}

/// Issues a signed resource token valid for `ttl`. Ports `GenerateToken`.
pub fn generate_token(
    master_key: &str,
    resource_link: &str,
    permissions: ResourcePermission,
    ttl: Duration,
) -> Result<String, anyhow::Error> {
    if master_key.trim().is_empty() {
        anyhow::bail!("master key must not be empty");
    }
    if ttl <= Duration::zero() {
        anyhow::bail!("Token TTL must be greater than zero.");
    }
    let master_key_bytes = base64::engine::general_purpose::STANDARD.decode(master_key)?;
    let normalized = normalize_resource_link(resource_link, false)?;
    let expires_at = Utc::now() + ttl;
    let signature = compute_signature(&master_key_bytes, &normalized, permissions, expires_at);
    let payload = ResourceTokenPayload {
        resource_link: normalized,
        permissions: permissions.as_str().to_string(),
        expires_at,
        signature,
    };
    let json = serde_json::to_vec(&payload)?;
    Ok(base64::engine::general_purpose::STANDARD.encode(&json))
}

/// Parses a token without validating its signature. Ports `ParseToken`.
pub fn parse_token(token: &str) -> Result<ResourceToken, anyhow::Error> {
    let payload = decode_payload(token)?;
    create_resource_token(&payload)
}

fn extract_token(auth_header: &str) -> Result<String, String> {
    if auth_header.trim().is_empty() {
        return Err("Missing Authorization header.".to_string());
    }
    let decoded = percent::decode(auth_header);
    let decoded = decoded.trim();
    if decoded.is_empty() {
        return Err("Missing Authorization header.".to_string());
    }

    // A bare token (not a structured `type=...&sig=...` header) is used directly.
    if !looks_structured(decoded) {
        return Ok(decoded.to_string());
    }

    let (kind, version, token) =
        parse_auth_header(decoded).ok_or("Invalid resource token header format.")?;
    if !kind.eq_ignore_ascii_case("resource") {
        return Err(format!("Unsupported auth type: {kind}"));
    }
    if !version.is_empty() && version != "1.0" {
        return Err(format!("Unsupported auth version: {version}"));
    }
    Ok(token)
}

fn looks_structured(header: &str) -> bool {
    header.to_ascii_lowercase().starts_with("type=") || header.contains('&')
}

fn validate_token(token: &str, master_key: &str) -> Result<ResourceToken, String> {
    let master_key_bytes = base64::engine::general_purpose::STANDARD
        .decode(master_key)
        .map_err(|e| format!("Invalid master key: {e}"))?;
    let payload = decode_payload(token).map_err(|e| e.to_string())?;
    let resource_token = create_resource_token(&payload).map_err(|e| e.to_string())?;
    let expected = compute_signature(
        &master_key_bytes,
        &resource_token.resource_link,
        resource_token.permissions,
        resource_token.expires_at,
    );
    if !fixed_time_eq(&payload.signature, &expected) {
        return Err("Invalid resource token signature.".to_string());
    }
    Ok(resource_token)
}

fn decode_payload(token: &str) -> Result<ResourceTokenPayload, anyhow::Error> {
    if token.trim().is_empty() {
        anyhow::bail!("Resource token is missing.");
    }
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(token.trim())
        .map_err(|_| anyhow::anyhow!("Resource token is not valid Base64."))?;
    let payload: ResourceTokenPayload = serde_json::from_slice(&bytes)
        .map_err(|_| anyhow::anyhow!("Invalid resource token payload."))?;
    if payload.resource_link.trim().is_empty() {
        anyhow::bail!("Resource token resourceLink is missing.");
    }
    if payload.permissions.trim().is_empty() {
        anyhow::bail!("Resource token permissions are missing.");
    }
    if payload.signature.trim().is_empty() {
        anyhow::bail!("Resource token signature is missing.");
    }
    Ok(payload)
}

fn create_resource_token(payload: &ResourceTokenPayload) -> Result<ResourceToken, anyhow::Error> {
    let permissions = ResourcePermission::parse(&payload.permissions).ok_or_else(|| {
        anyhow::anyhow!(
            "Unsupported resource token permissions: {}",
            payload.permissions
        )
    })?;
    Ok(ResourceToken {
        resource_link: normalize_resource_link(&payload.resource_link, false)?,
        permissions,
        expires_at: payload.expires_at,
    })
}

fn compute_signature(
    master_key_bytes: &[u8],
    resource_link: &str,
    permissions: ResourcePermission,
    expires_at: DateTime<Utc>,
) -> String {
    let normalized = normalize_resource_link(resource_link, false).unwrap_or_default();
    let payload = format!(
        "{}\n{}\n{}",
        normalized,
        permissions.as_str(),
        expires_at.to_rfc3339()
    );
    let mut mac =
        HmacSha256::new_from_slice(master_key_bytes).expect("HMAC accepts any key length");
    mac.update(payload.as_bytes());
    base64::engine::general_purpose::STANDARD.encode(mac.finalize().into_bytes())
}

/// Normalizes a resource link: trims surrounding whitespace and slashes, and
/// lowercases. Ports `NormalizeResourceLink`.
pub fn normalize_resource_link(
    resource_link: &str,
    allow_empty: bool,
) -> Result<String, anyhow::Error> {
    let normalized = resource_link.trim().trim_matches('/').to_ascii_lowercase();
    if !allow_empty && normalized.is_empty() {
        anyhow::bail!("Resource token resourceLink is missing.");
    }
    Ok(normalized)
}

fn is_resource_link_match(granted: &str, requested: &str) -> bool {
    let granted = normalize_resource_link(granted, true).unwrap_or_default();
    let requested = normalize_resource_link(requested, true).unwrap_or_default();
    if requested.is_empty() {
        return granted.is_empty();
    }
    if granted == requested {
        return true;
    }
    requested.starts_with(&format!("{granted}/"))
}

fn is_permission_allowed(permissions: ResourcePermission, verb: &str) -> bool {
    if permissions == ResourcePermission::All {
        return true;
    }
    verb.eq_ignore_ascii_case("GET") || verb.eq_ignore_ascii_case("HEAD")
}

fn fixed_time_eq(provided: &str, expected: &str) -> bool {
    let provided = match base64::engine::general_purpose::STANDARD.decode(provided) {
        Ok(bytes) => bytes,
        Err(_) => return false,
    };
    let expected = match base64::engine::general_purpose::STANDARD.decode(expected) {
        Ok(bytes) => bytes,
        Err(_) => return false,
    };
    if provided.len() != expected.len() {
        return false;
    }
    let mut diff = 0u8;
    for (a, b) in provided.iter().zip(expected.iter()) {
        diff |= a ^ b;
    }
    diff == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY: &str =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    #[tokio::test]
    async fn generated_token_validates_for_granted_resource() {
        let token = generate_token(
            KEY,
            "dbs/db1/colls/c1",
            ResourcePermission::All,
            Duration::hours(1),
        )
        .unwrap();
        let provider = ResourceTokenProvider::new(KEY);
        let result = provider
            .validate(&token, "POST", "docs", "dbs/db1/colls/c1/docs", "x")
            .await;
        assert!(result.is_authenticated, "{:?}", result.error_message);
        assert_eq!(result.auth_type, Some(AuthType::ResourceToken));
    }

    #[tokio::test]
    async fn read_token_rejects_writes() {
        let token = generate_token(
            KEY,
            "dbs/db1/colls/c1",
            ResourcePermission::Read,
            Duration::hours(1),
        )
        .unwrap();
        let provider = ResourceTokenProvider::new(KEY);
        let write = provider
            .validate(&token, "POST", "docs", "dbs/db1/colls/c1/docs", "x")
            .await;
        assert!(!write.is_authenticated);
        let read = provider
            .validate(&token, "GET", "colls", "dbs/db1/colls/c1", "x")
            .await;
        assert!(read.is_authenticated);
    }

    #[tokio::test]
    async fn token_for_other_resource_is_rejected() {
        let token = generate_token(
            KEY,
            "dbs/db1/colls/c1",
            ResourcePermission::All,
            Duration::hours(1),
        )
        .unwrap();
        let provider = ResourceTokenProvider::new(KEY);
        let result = provider
            .validate(&token, "GET", "colls", "dbs/db1/colls/other", "x")
            .await;
        assert!(!result.is_authenticated);
    }

    #[tokio::test]
    async fn expired_token_is_rejected() {
        // Craft a correctly-signed token whose expiry is in the past.
        let master_key_bytes = base64::engine::general_purpose::STANDARD
            .decode(KEY)
            .unwrap();
        let resource_link = "dbs/db1".to_string();
        let expires_at = Utc::now() - Duration::hours(1);
        let signature = compute_signature(
            &master_key_bytes,
            &resource_link,
            ResourcePermission::All,
            expires_at,
        );
        let payload = ResourceTokenPayload {
            resource_link,
            permissions: ResourcePermission::All.as_str().to_string(),
            expires_at,
            signature,
        };
        let json = serde_json::to_vec(&payload).unwrap();
        let token = base64::engine::general_purpose::STANDARD.encode(&json);

        let provider = ResourceTokenProvider::new(KEY);
        let result = provider
            .validate(&token, "GET", "dbs", "dbs/db1", "x")
            .await;
        assert!(!result.is_authenticated);
        assert_eq!(
            result.error_message.as_deref(),
            Some("Resource token has expired.")
        );
    }

    #[tokio::test]
    async fn wrong_key_fails_signature() {
        let token =
            generate_token(KEY, "dbs/db1", ResourcePermission::All, Duration::hours(1)).unwrap();
        let other = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        let provider = ResourceTokenProvider::new(other);
        let result = provider
            .validate(&token, "GET", "dbs", "dbs/db1", "x")
            .await;
        assert!(!result.is_authenticated);
    }

    #[test]
    fn parse_token_round_trip() {
        let token = generate_token(
            KEY,
            "dbs/DB1/colls/C1",
            ResourcePermission::Read,
            Duration::hours(2),
        )
        .unwrap();
        let parsed = parse_token(&token).unwrap();
        assert_eq!(parsed.resource_link, "dbs/db1/colls/c1");
        assert_eq!(parsed.permissions, ResourcePermission::Read);
    }
}
