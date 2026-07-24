//! Chained authentication. Ports `CompositeAuthProvider`.

use async_trait::async_trait;
use cosmos_core::traits::{AuthProvider, AuthResult};

/// Tries each inner provider in order, returning the first success (or the last
/// failure). Ports `CompositeAuthProvider`.
pub struct CompositeAuthProvider {
    providers: Vec<Box<dyn AuthProvider>>,
}

impl CompositeAuthProvider {
    pub fn new(providers: Vec<Box<dyn AuthProvider>>) -> Self {
        Self { providers }
    }
}

#[async_trait]
impl AuthProvider for CompositeAuthProvider {
    async fn validate(
        &self,
        auth_header: &str,
        verb: &str,
        resource_type: &str,
        resource_link: &str,
        date_header: &str,
    ) -> AuthResult {
        let mut last_failure: Option<AuthResult> = None;
        for provider in &self.providers {
            let result = provider
                .validate(auth_header, verb, resource_type, resource_link, date_header)
                .await;
            if result.is_authenticated {
                return result;
            }
            last_failure = Some(result);
        }
        last_failure
            .unwrap_or_else(|| AuthResult::failure("No authentication providers configured."))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::master_key::MasterKeyAuthProvider;
    use crate::resource_token::{generate_token, ResourcePermission, ResourceTokenProvider};
    use chrono::Duration;
    use cosmos_core::traits::AuthType;

    const KEY: &str =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    #[tokio::test]
    async fn falls_through_to_resource_token_provider() {
        let composite = CompositeAuthProvider::new(vec![
            Box::new(MasterKeyAuthProvider::new(KEY)),
            Box::new(ResourceTokenProvider::new(KEY)),
        ]);
        let token =
            generate_token(KEY, "dbs/db1", ResourcePermission::All, Duration::hours(1)).unwrap();
        let result = composite
            .validate(&token, "GET", "dbs", "dbs/db1", "x")
            .await;
        assert!(result.is_authenticated, "{:?}", result.error_message);
        assert_eq!(result.auth_type, Some(AuthType::ResourceToken));
    }

    #[tokio::test]
    async fn empty_provider_list_fails() {
        let composite = CompositeAuthProvider::new(vec![]);
        let result = composite
            .validate("anything", "GET", "dbs", "dbs/db1", "x")
            .await;
        assert!(!result.is_authenticated);
    }
}
