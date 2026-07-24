//! Default consistency manager. Ports `ConsistencyManager.cs`.
//!
//! Consistency ordering follows the `ConsistencyLevel` enum discriminants
//! (Strong < BoundedStaleness < Session < ConsistentPrefix < Eventual), so a
//! *larger* value is a *weaker* level. `is_valid_consistency_level` accepts any
//! requested level that is the same or weaker than the account default.

use std::collections::HashMap;
use std::sync::Mutex;

use crate::traits::ConsistencyManager as ConsistencyManagerTrait;
use crate::ConsistencyLevel;

/// Default implementation of consistency-level management with per-container
/// LSN tracking for session tokens.
pub struct ConsistencyManager {
    default_level: ConsistencyLevel,
    container_lsns: Mutex<HashMap<String, i64>>,
}

impl ConsistencyManager {
    pub fn new(default_level: ConsistencyLevel) -> Self {
        Self {
            default_level,
            container_lsns: Mutex::new(HashMap::new()),
        }
    }

    fn parse_lsn(session_token: &str) -> Option<i64> {
        let parts: Vec<&str> = session_token.split(':').collect();
        if parts.len() == 2 {
            parts[1].parse::<i64>().ok()
        } else {
            None
        }
    }
}

impl Default for ConsistencyManager {
    fn default() -> Self {
        Self::new(ConsistencyLevel::Session)
    }
}

impl ConsistencyManagerTrait for ConsistencyManager {
    fn default_consistency_level(&self) -> ConsistencyLevel {
        self.default_level
    }

    fn is_valid_consistency_level(&self, requested: ConsistencyLevel) -> bool {
        // Clients may request the same or a weaker level than the default.
        requested >= self.default_level
    }

    fn effective_consistency(&self, requested: Option<ConsistencyLevel>) -> ConsistencyLevel {
        match requested {
            None => self.default_level,
            Some(level) if self.is_valid_consistency_level(level) => level,
            Some(_) => self.default_level,
        }
    }

    fn generate_session_token(&self, database_id: &str, container_id: &str, lsn: i64) -> String {
        let key = format!("{database_id}/{container_id}");
        let mut lsns = self.container_lsns.lock().unwrap();
        let entry = lsns.entry(key).or_insert(lsn);
        *entry = (*entry).max(lsn);
        // Format: "partitionIndex:lsn".
        format!("0:{lsn}")
    }

    fn validate_session_token(
        &self,
        database_id: &str,
        container_id: &str,
        session_token: Option<&str>,
    ) -> bool {
        let token = match session_token {
            Some(t) if !t.is_empty() => t,
            _ => return true, // No token => no session requirement.
        };
        let key = format!("{database_id}/{container_id}");
        let lsns = self.container_lsns.lock().unwrap();
        let current = match lsns.get(&key) {
            Some(&lsn) => lsn,
            None => return true, // Container not yet seen.
        };
        match Self::parse_lsn(token) {
            Some(requested) => requested <= current,
            None => false,
        }
    }

    fn current_session_token(&self, database_id: &str, container_id: &str) -> String {
        let key = format!("{database_id}/{container_id}");
        let mut lsns = self.container_lsns.lock().unwrap();
        let lsn = *lsns.entry(key).or_insert(0);
        format!("0:{lsn}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_level_is_session() {
        let cm = ConsistencyManager::default();
        assert_eq!(cm.default_consistency_level(), ConsistencyLevel::Session);
    }

    #[test]
    fn weaker_levels_are_valid_stronger_are_not() {
        let cm = ConsistencyManager::new(ConsistencyLevel::Session);
        // Same or weaker than Session is allowed.
        assert!(cm.is_valid_consistency_level(ConsistencyLevel::Session));
        assert!(cm.is_valid_consistency_level(ConsistencyLevel::Eventual));
        // Stronger than Session is rejected.
        assert!(!cm.is_valid_consistency_level(ConsistencyLevel::Strong));
    }

    #[test]
    fn session_token_validation_respects_lsn() {
        let cm = ConsistencyManager::new(ConsistencyLevel::Session);
        cm.generate_session_token("db", "coll", 5);
        assert!(cm.validate_session_token("db", "coll", Some("0:3")));
        assert!(cm.validate_session_token("db", "coll", Some("0:5")));
        assert!(!cm.validate_session_token("db", "coll", Some("0:9")));
        // Unknown container accepts any token.
        assert!(cm.validate_session_token("db", "other", Some("0:99")));
    }
}
