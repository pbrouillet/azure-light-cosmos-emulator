//! Emulator DML command surface (`INSERT`, `UPDATE`, `DELETE`).
//!
//! Cosmos DB SQL is SELECT-only; this mirrors the .NET emulator convenience
//! service by translating DML into `DocumentStore` operations.

use std::collections::HashMap;
use std::sync::Arc;

use cosmos_core::error::CosmosError;
use cosmos_core::models::{FeedResponse, JsonObject, PartitionKeyValue};
use cosmos_core::traits::{DocumentStore, QueryEngine, QueryOptions};
use cosmos_core::CosmosResult;
use serde_json::Value;

pub struct DmlCommandService {
    store: Arc<dyn DocumentStore>,
    query_engine: Arc<dyn QueryEngine>,
}

impl DmlCommandService {
    pub fn new(store: Arc<dyn DocumentStore>, query_engine: Arc<dyn QueryEngine>) -> Self {
        Self {
            store,
            query_engine,
        }
    }

    pub fn is_dml(sql: &str) -> bool {
        let clean = strip_comments(sql)
            .trim()
            .trim_end_matches(';')
            .trim()
            .to_ascii_uppercase();
        clean.starts_with("INSERT") || clean.starts_with("UPDATE") || clean.starts_with("DELETE")
    }

    pub async fn execute(
        &self,
        database_id: &str,
        container_id: &str,
        sql: &str,
        parameters: Option<&HashMap<String, Value>>,
    ) -> CosmosResult<FeedResponse<JsonObject>> {
        let clean = strip_comments(sql)
            .trim()
            .trim_end_matches(';')
            .trim()
            .to_string();
        if clean.to_ascii_uppercase().starts_with("INSERT") {
            return self
                .execute_insert(database_id, container_id, &clean, parameters)
                .await;
        }
        if clean.to_ascii_uppercase().starts_with("UPDATE") {
            return self
                .execute_update(database_id, container_id, &clean, parameters)
                .await;
        }
        if clean.to_ascii_uppercase().starts_with("DELETE") {
            return self
                .execute_delete(database_id, container_id, &clean, parameters)
                .await;
        }
        Err(CosmosError::bad_request(
            "Unsupported statement. Use INSERT, UPDATE, DELETE, or SELECT.",
        ))
    }

    async fn execute_insert(
        &self,
        database_id: &str,
        container_id: &str,
        sql: &str,
        parameters: Option<&HashMap<String, Value>>,
    ) -> CosmosResult<FeedResponse<JsonObject>> {
        let values_index = index_of_keyword(sql, "VALUES", 0).ok_or_else(|| {
            CosmosError::bad_request(
                "INSERT syntax: INSERT INTO <alias> VALUES ({...}) or VALUES (@param)",
            )
        })?;
        let mut values = sql[values_index + "VALUES".len()..].trim();
        if values.starts_with('(') && values.ends_with(')') {
            values = values[1..values.len() - 1].trim();
        }
        let value = if values.starts_with('@') {
            parameters
                .and_then(|p| p.get(values))
                .cloned()
                .ok_or_else(|| {
                    CosmosError::bad_request(format!("Parameter '{values}' is not defined."))
                })?
        } else {
            serde_json::from_str(values).map_err(|e| CosmosError::bad_request(e.to_string()))?
        };
        let Value::Object(document) = value else {
            return Err(CosmosError::bad_request(
                "INSERT VALUES must be a JSON object or @parameter.",
            ));
        };
        let created = self
            .store
            .create_document(database_id, container_id, document, None)
            .await?;
        let mut response = FeedResponse::new(vec![created.to_response_body()]);
        response.rid = format!("{database_id}/{container_id}");
        Ok(response)
    }

    async fn execute_update(
        &self,
        database_id: &str,
        container_id: &str,
        sql: &str,
        parameters: Option<&HashMap<String, Value>>,
    ) -> CosmosResult<FeedResponse<JsonObject>> {
        let set_index = index_of_keyword(sql, "SET", "UPDATE".len()).ok_or_else(|| {
            CosmosError::bad_request(
                "UPDATE syntax: UPDATE <alias> SET <field> = <value> [, ...] [WHERE <conditions>]",
            )
        })?;
        let alias = sql["UPDATE".len()..set_index].trim();
        let alias = if alias.is_empty() { "c" } else { alias };
        let where_index = index_of_keyword(sql, "WHERE", set_index + "SET".len());
        let set_clause = match where_index {
            Some(i) => sql[set_index + "SET".len()..i].trim(),
            None => sql[set_index + "SET".len()..].trim(),
        };
        let assignments = parse_assignments(set_clause, alias, parameters)?;
        let select_query = match where_index {
            Some(i) => format!("SELECT * FROM {alias} {}", &sql[i..]),
            None => format!("SELECT * FROM {alias}"),
        };
        let matched = self
            .query_engine
            .execute_query(
                database_id,
                container_id,
                &select_query,
                parameters,
                Some(QueryOptions {
                    enable_scan: true,
                    enable_cross_partition_query: true,
                    ..Default::default()
                }),
            )
            .await?;
        let mut resources = Vec::new();
        for mut doc in matched.resources {
            for (path, value) in &assignments {
                set_path(&mut doc, path, value.clone());
            }
            let id = doc
                .get("id")
                .and_then(Value::as_str)
                .ok_or_else(|| CosmosError::bad_request("Matched document has no 'id' field."))?
                .to_string();
            let replaced = self
                .store
                .replace_document(database_id, container_id, &id, doc, None, None)
                .await?;
            resources.push(replaced.to_response_body());
        }
        Ok(FeedResponse::new(resources))
    }

    async fn execute_delete(
        &self,
        database_id: &str,
        container_id: &str,
        sql: &str,
        parameters: Option<&HashMap<String, Value>>,
    ) -> CosmosResult<FeedResponse<JsonObject>> {
        let from_index = index_of_keyword(sql, "FROM", "DELETE".len()).ok_or_else(|| {
            CosmosError::bad_request("DELETE syntax: DELETE FROM <alias> [WHERE <conditions>]")
        })?;
        let where_index = index_of_keyword(sql, "WHERE", from_index + "FROM".len());
        let alias = match where_index {
            Some(i) => sql[from_index + "FROM".len()..i].trim(),
            None => sql[from_index + "FROM".len()..].trim(),
        };
        let alias = if alias.is_empty() { "c" } else { alias };
        let select_query = match where_index {
            Some(i) => format!("SELECT * FROM {alias} {}", &sql[i..]),
            None => format!("SELECT * FROM {alias}"),
        };
        let matched = self
            .query_engine
            .execute_query(
                database_id,
                container_id,
                &select_query,
                parameters,
                Some(QueryOptions {
                    enable_scan: true,
                    enable_cross_partition_query: true,
                    ..Default::default()
                }),
            )
            .await?;
        let container = self.store.get_container(database_id, container_id).await?;
        let mut resources = Vec::new();
        for doc in matched.resources {
            let id = doc
                .get("id")
                .and_then(Value::as_str)
                .ok_or_else(|| CosmosError::bad_request("Matched document has no 'id' field."))?
                .to_string();
            let pk = extract_partition_key(&doc, &container.partition_key.paths);
            resources.push(doc.clone());
            self.store
                .delete_document(database_id, container_id, &id, &pk)
                .await?;
        }
        Ok(FeedResponse::new(resources))
    }
}

fn strip_comments(sql: &str) -> String {
    sql.lines()
        .map(|line| line.split_once("--").map_or(line, |(before, _)| before))
        .collect::<Vec<_>>()
        .join("\n")
}

fn index_of_keyword(sql: &str, keyword: &str, start: usize) -> Option<usize> {
    let lower = sql.to_ascii_lowercase();
    let needle = keyword.to_ascii_lowercase();
    let mut pos = start;
    while pos < sql.len() {
        let idx = lower[pos..].find(&needle).map(|i| i + pos)?;
        let before = idx == 0 || !sql.as_bytes()[idx - 1].is_ascii_alphanumeric();
        let after_idx = idx + keyword.len();
        let after = after_idx >= sql.len() || !sql.as_bytes()[after_idx].is_ascii_alphanumeric();
        if before && after {
            return Some(idx);
        }
        pos = idx + 1;
    }
    None
}

fn parse_assignments(
    set_clause: &str,
    alias: &str,
    parameters: Option<&HashMap<String, Value>>,
) -> CosmosResult<Vec<(Vec<String>, Value)>> {
    split_top_level(set_clause, ',')
        .into_iter()
        .map(|part| {
            let (lhs, rhs) = part.split_once('=').ok_or_else(|| {
                CosmosError::bad_request(format!("Invalid SET assignment: '{}'.", part.trim()))
            })?;
            let mut lhs = lhs.trim();
            let prefix = format!("{alias}.");
            if lhs
                .to_ascii_lowercase()
                .starts_with(&prefix.to_ascii_lowercase())
            {
                lhs = &lhs[prefix.len()..];
            }
            let path = lhs.split('.').map(str::to_string).collect::<Vec<_>>();
            if path.is_empty() || path.iter().any(String::is_empty) {
                return Err(CosmosError::bad_request(format!(
                    "Invalid field path: '{lhs}'."
                )));
            }
            Ok((path, parse_value(rhs.trim(), parameters)?))
        })
        .collect()
}

fn parse_value(rhs: &str, parameters: Option<&HashMap<String, Value>>) -> CosmosResult<Value> {
    if rhs.starts_with('@') {
        return parameters
            .and_then(|p| p.get(rhs))
            .cloned()
            .ok_or_else(|| CosmosError::bad_request(format!("Parameter '{rhs}' is not defined.")));
    }
    serde_json::from_str(rhs).or_else(|_| {
        if (rhs.starts_with('"') && rhs.ends_with('"'))
            || (rhs.starts_with('\'') && rhs.ends_with('\''))
        {
            Ok(Value::String(rhs[1..rhs.len() - 1].to_string()))
        } else {
            Err(CosmosError::bad_request(format!(
                "Cannot parse SET value: '{rhs}'."
            )))
        }
    })
}

fn split_top_level(input: &str, delimiter: char) -> Vec<String> {
    let (mut depth, mut in_single, mut in_double, mut start) = (0i32, false, false, 0usize);
    let chars: Vec<char> = input.chars().collect();
    let mut parts = Vec::new();
    for (i, ch) in chars.iter().copied().enumerate() {
        match ch {
            '\'' if !in_double => in_single = !in_single,
            '"' if !in_single => in_double = !in_double,
            '(' | '[' | '{' if !in_single && !in_double => depth += 1,
            ')' | ']' | '}' if !in_single && !in_double => depth -= 1,
            c if c == delimiter && depth == 0 && !in_single && !in_double => {
                parts.push(chars[start..i].iter().collect());
                start = i + 1;
            }
            _ => {}
        }
    }
    if start < chars.len() {
        parts.push(chars[start..].iter().collect());
    }
    parts
}

fn set_path(doc: &mut JsonObject, path: &[String], value: Value) {
    if path.len() == 1 {
        doc.insert(path[0].clone(), value);
        return;
    }
    let entry = doc
        .entry(path[0].clone())
        .or_insert_with(|| Value::Object(JsonObject::new()));
    if !entry.is_object() {
        *entry = Value::Object(JsonObject::new());
    }
    if let Value::Object(child) = entry {
        set_path(child, &path[1..], value);
    }
}

fn extract_partition_key(document: &JsonObject, paths: &[String]) -> PartitionKeyValue {
    let values = paths
        .iter()
        .map(|path| {
            document
                .get(path.trim_start_matches('/'))
                .cloned()
                .unwrap_or(Value::Null)
        })
        .collect();
    PartitionKeyValue::multi(values)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_dml() {
        assert!(DmlCommandService::is_dml("-- c\nINSERT INTO c VALUES ({})"));
        assert!(DmlCommandService::is_dml("UPDATE c SET c.x = 1"));
        assert!(DmlCommandService::is_dml("DELETE FROM c"));
        assert!(!DmlCommandService::is_dml("SELECT * FROM c"));
    }

    #[test]
    fn parses_nested_assignments() {
        let parsed = parse_assignments("c.name = 'Ada', c.nested.count = 3", "c", None).unwrap();
        assert_eq!(parsed[0].0, vec!["name"]);
        assert_eq!(parsed[0].1, Value::String("Ada".into()));
        assert_eq!(parsed[1].0, vec!["nested", "count"]);
        assert_eq!(parsed[1].1, Value::from(3));
    }
}
