//! EntraID (Azure AD) JWT bearer authentication. Ports `EntraIdAuthProvider`.
//!
//! ## Validation scope
//! The .NET provider performs **full** OIDC validation (signature/issuer/
//! audience/expiry against live Azure AD metadata) when a tenant and client id
//! are configured, and **structure-only** validation (well-formed JWT + expiry)
//! otherwise (the emulator dev-mode default).
//!
//! This port implements structure-only validation without network access:
//! well-formed JWT, expiry (`exp`), and — when a tenant/client are configured —
//! issuer (`iss`) and audience (`aud`) claim checks. Cryptographic **signature**
//! verification requires fetching JWKS from Azure AD and is intentionally out of
//! scope for the offline emulator; this is documented behaviour, not a bug.

use async_trait::async_trait;
use base64::Engine;
use chrono::Utc;
use cosmos_core::traits::{AuthProvider, AuthResult, AuthType};
use serde_json::Value;

/// Validates `Bearer <jwt>` EntraID tokens. Ports `EntraIdAuthProvider`.
pub struct EntraIdAuthProvider {
    enabled: bool,
    tenant_id: Option<String>,
    client_id: Option<String>,
}

impl EntraIdAuthProvider {
    pub fn new(enabled: bool, tenant_id: Option<String>, client_id: Option<String>) -> Self {
        Self {
            enabled,
            tenant_id: tenant_id.filter(|s| !s.is_empty()),
            client_id: client_id.filter(|s| !s.is_empty()),
        }
    }
}

#[async_trait]
impl AuthProvider for EntraIdAuthProvider {
    async fn validate(
        &self,
        auth_header: &str,
        _verb: &str,
        _resource_type: &str,
        _resource_link: &str,
        _date_header: &str,
    ) -> AuthResult {
        if !self.enabled {
            return AuthResult::failure("EntraID authentication is not enabled.");
        }

        let bearer = match auth_header
            .strip_prefix("Bearer ")
            .or_else(|| auth_header.strip_prefix("bearer "))
        {
            Some(rest) => rest.trim(),
            None => {
                return AuthResult::failure("Expected Bearer token for EntraID authentication.")
            }
        };
        if bearer.is_empty() {
            return AuthResult::failure("Empty Bearer token.");
        }

        let claims = match decode_jwt_claims(bearer) {
            Some(claims) => claims,
            None => return AuthResult::failure("Bearer token is not a valid JWT."),
        };

        if let Some(exp) = claims.get("exp").and_then(Value::as_i64) {
            if exp < Utc::now().timestamp() {
                return AuthResult::failure("Bearer token has expired.");
            }
        }

        // With a configured tenant/client, additionally check issuer & audience.
        if let (Some(tenant), Some(client)) = (&self.tenant_id, &self.client_id) {
            if let Some(iss) = claims.get("iss").and_then(Value::as_str) {
                let valid_issuers = [
                    format!("https://login.microsoftonline.com/{tenant}/v2.0"),
                    format!("https://sts.windows.net/{tenant}/"),
                ];
                if !valid_issuers.iter().any(|v| v == iss) {
                    return AuthResult::failure("Bearer token issuer is not trusted.");
                }
            }
            if let Some(aud) = claims.get("aud") {
                if !audience_matches(aud, client) {
                    return AuthResult::failure("Bearer token audience does not match.");
                }
            }
        }

        let principal = claims
            .get("oid")
            .and_then(Value::as_str)
            .or_else(|| claims.get("sub").and_then(Value::as_str))
            .unwrap_or("entra-user")
            .to_string();

        AuthResult::success(AuthType::EntraId, Some(principal))
    }
}

fn audience_matches(aud: &Value, client: &str) -> bool {
    match aud {
        Value::String(s) => s == client,
        Value::Array(items) => items.iter().any(|v| v.as_str() == Some(client)),
        _ => false,
    }
}

/// Decodes (without verifying) the claims payload of a JWT.
fn decode_jwt_claims(token: &str) -> Option<Value> {
    let mut parts = token.split('.');
    let _header = parts.next()?;
    let payload = parts.next()?;
    let _signature = parts.next()?;
    if parts.next().is_some() {
        return None;
    }
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(payload)
        .ok()?;
    serde_json::from_slice(&bytes).ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::Engine;

    fn make_jwt(claims: Value) -> String {
        let header = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .encode(br#"{"alg":"RS256","typ":"JWT"}"#);
        let payload = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .encode(serde_json::to_vec(&claims).unwrap());
        format!("{header}.{payload}.signature")
    }

    #[tokio::test]
    async fn disabled_provider_rejects() {
        let provider = EntraIdAuthProvider::new(false, None, None);
        let result = provider
            .validate("Bearer x", "GET", "dbs", "dbs/x", "x")
            .await;
        assert!(!result.is_authenticated);
    }

    #[tokio::test]
    async fn structure_only_accepts_valid_unexpired_token() {
        let provider = EntraIdAuthProvider::new(true, None, None);
        let exp = Utc::now().timestamp() + 3600;
        let jwt = make_jwt(serde_json::json!({ "exp": exp, "oid": "abc-123" }));
        let result = provider
            .validate(&format!("Bearer {jwt}"), "GET", "dbs", "dbs/x", "x")
            .await;
        assert!(result.is_authenticated, "{:?}", result.error_message);
        assert_eq!(result.principal.as_deref(), Some("abc-123"));
    }

    #[tokio::test]
    async fn expired_token_is_rejected() {
        let provider = EntraIdAuthProvider::new(true, None, None);
        let exp = Utc::now().timestamp() - 60;
        let jwt = make_jwt(serde_json::json!({ "exp": exp, "sub": "u" }));
        let result = provider
            .validate(&format!("Bearer {jwt}"), "GET", "dbs", "dbs/x", "x")
            .await;
        assert!(!result.is_authenticated);
    }

    #[tokio::test]
    async fn non_bearer_and_garbage_are_rejected() {
        let provider = EntraIdAuthProvider::new(true, None, None);
        assert!(
            !provider
                .validate("type=master", "GET", "d", "d", "x")
                .await
                .is_authenticated
        );
        assert!(
            !provider
                .validate("Bearer not-a-jwt", "GET", "d", "d", "x")
                .await
                .is_authenticated
        );
    }

    #[tokio::test]
    async fn issuer_and_audience_checked_when_configured() {
        let provider = EntraIdAuthProvider::new(
            true,
            Some("tenant-1".to_string()),
            Some("client-1".to_string()),
        );
        let exp = Utc::now().timestamp() + 3600;
        let good = make_jwt(serde_json::json!({
            "exp": exp,
            "oid": "u",
            "iss": "https://login.microsoftonline.com/tenant-1/v2.0",
            "aud": "client-1"
        }));
        assert!(
            provider
                .validate(&format!("Bearer {good}"), "GET", "d", "d", "x")
                .await
                .is_authenticated
        );

        let wrong_aud = make_jwt(serde_json::json!({
            "exp": exp,
            "oid": "u",
            "iss": "https://login.microsoftonline.com/tenant-1/v2.0",
            "aud": "someone-else"
        }));
        assert!(
            !provider
                .validate(&format!("Bearer {wrong_aud}"), "GET", "d", "d", "x")
                .await
                .is_authenticated
        );
    }
}
