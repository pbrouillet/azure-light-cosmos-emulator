//! Integration tests for the Cosmos SQL query engine.

use std::collections::HashMap;

use cosmos_query::run_query;
use serde_json::{json, Value};

fn docs() -> Vec<Value> {
    vec![
        json!({"id": "1", "name": "Alice", "age": 30, "city": "Paris", "tags": ["a", "b"]}),
        json!({"id": "2", "name": "Bob", "age": 25, "city": "London", "tags": ["b", "c"]}),
        json!({"id": "3", "name": "Carol", "age": 35, "city": "Paris"}),
        json!({"id": "4", "name": "Dave", "age": 25, "city": "Berlin", "tags": []}),
    ]
}

fn run(q: &str) -> Vec<Value> {
    run_query(q, &docs(), &HashMap::new()).expect("query should succeed")
}

fn run_p(q: &str, params: HashMap<String, Value>) -> Vec<Value> {
    run_query(q, &docs(), &params).expect("query should succeed")
}

#[test]
fn select_star_returns_all() {
    let rows = run("SELECT * FROM c");
    assert_eq!(rows.len(), 4);
    assert_eq!(rows[0]["name"], json!("Alice"));
}

#[test]
fn where_equality_filters() {
    let rows = run("SELECT c.name FROM c WHERE c.city = 'Paris'");
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0], json!({"name": "Alice"}));
    assert_eq!(rows[1], json!({"name": "Carol"}));
}

#[test]
fn where_numeric_comparison() {
    let rows = run("SELECT c.name FROM c WHERE c.age >= 30");
    let names: Vec<&str> = rows.iter().map(|r| r["name"].as_str().unwrap()).collect();
    assert_eq!(names, vec!["Alice", "Carol"]);
}

#[test]
fn and_or_logic() {
    let rows = run("SELECT c.name FROM c WHERE c.city = 'Paris' AND c.age > 30");
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0]["name"], json!("Carol"));

    let rows = run("SELECT c.name FROM c WHERE c.city = 'Berlin' OR c.city = 'London'");
    assert_eq!(rows.len(), 2);
}

#[test]
fn select_value_projection() {
    let rows = run("SELECT VALUE c.name FROM c WHERE c.age = 25");
    let vals: Vec<&Value> = rows.iter().map(|r| &r["$1"]).collect();
    assert_eq!(vals, vec![&json!("Bob"), &json!("Dave")]);
}

#[test]
fn aliases_and_multiple_fields() {
    let rows = run("SELECT c.name AS n, c.age FROM c WHERE c.id = '1'");
    assert_eq!(rows[0], json!({"n": "Alice", "age": 30}));
}

#[test]
fn order_by_desc() {
    let rows = run("SELECT c.name FROM c ORDER BY c.age DESC");
    let names: Vec<&str> = rows.iter().map(|r| r["name"].as_str().unwrap()).collect();
    assert_eq!(names[0], "Carol");
    assert_eq!(names[1], "Alice");
}

#[test]
fn order_by_multikey() {
    let rows = run("SELECT c.name FROM c ORDER BY c.age ASC, c.name DESC");
    let names: Vec<&str> = rows.iter().map(|r| r["name"].as_str().unwrap()).collect();
    // age 25: Dave, Bob (name desc); age 30: Alice; age 35: Carol
    assert_eq!(names, vec!["Dave", "Bob", "Alice", "Carol"]);
}

#[test]
fn top_and_offset_limit() {
    let rows = run("SELECT TOP 2 c.name FROM c ORDER BY c.age ASC");
    assert_eq!(rows.len(), 2);

    let rows = run("SELECT c.name FROM c ORDER BY c.age ASC OFFSET 1 LIMIT 2");
    let names: Vec<&str> = rows.iter().map(|r| r["name"].as_str().unwrap()).collect();
    // Stable age-asc order is [Bob(25), Dave(25), Alice(30), Carol(35)];
    // OFFSET 1 drops Bob, LIMIT 2 keeps Dave, Alice.
    assert_eq!(names, vec!["Dave", "Alice"]);
}

#[test]
fn parameters() {
    let mut params = HashMap::new();
    params.insert("@city".to_string(), json!("Paris"));
    let rows = run_p("SELECT c.name FROM c WHERE c.city = @city", params);
    assert_eq!(rows.len(), 2);
}

#[test]
fn in_and_between() {
    let rows = run("SELECT c.name FROM c WHERE c.city IN ('Paris', 'Berlin')");
    assert_eq!(rows.len(), 3);

    let rows = run("SELECT c.name FROM c WHERE c.age BETWEEN 26 AND 34");
    assert_eq!(rows.len(), 1);
    assert_eq!(rows[0]["name"], json!("Alice"));
}

#[test]
fn scalar_functions() {
    let rows = run("SELECT VALUE UPPER(c.name) FROM c WHERE c.id = '1'");
    assert_eq!(rows[0]["$1"], json!("ALICE"));

    let rows = run("SELECT c.name FROM c WHERE STARTSWITH(c.name, 'C')");
    assert_eq!(rows[0]["name"], json!("Carol"));

    let rows = run("SELECT c.name FROM c WHERE CONTAINS(c.city, 'ondon')");
    assert_eq!(rows[0]["name"], json!("Bob"));

    let rows = run("SELECT c.name FROM c WHERE ARRAY_CONTAINS(c.tags, 'c')");
    assert_eq!(rows[0]["name"], json!("Bob"));
}

#[test]
fn is_defined_filters_missing() {
    let rows = run("SELECT c.name FROM c WHERE IS_DEFINED(c.tags)");
    let names: Vec<&str> = rows.iter().map(|r| r["name"].as_str().unwrap()).collect();
    assert_eq!(names, vec!["Alice", "Bob", "Dave"]);
}

#[test]
fn arithmetic_projection() {
    let rows = run("SELECT VALUE c.age * 2 FROM c WHERE c.id = '2'");
    assert_eq!(rows[0]["$1"], json!(50));
}

#[test]
fn aggregate_count() {
    let rows = run("SELECT VALUE COUNT(1) FROM c WHERE c.city = 'Paris'");
    assert_eq!(rows[0]["$1"], json!(2));
}

#[test]
fn aggregate_sum_avg_min_max() {
    let rows = run("SELECT VALUE SUM(c.age) FROM c");
    assert_eq!(rows[0]["$1"], json!(115));

    let rows = run("SELECT VALUE AVG(c.age) FROM c WHERE c.age = 25");
    assert_eq!(rows[0]["$1"], json!(25));

    let rows = run("SELECT VALUE MIN(c.age) FROM c");
    assert_eq!(rows[0]["$1"], json!(25));

    let rows = run("SELECT VALUE MAX(c.age) FROM c");
    assert_eq!(rows[0]["$1"], json!(35));
}

#[test]
fn aggregate_named() {
    let rows = run("SELECT COUNT(1) AS total FROM c");
    assert_eq!(rows[0], json!({"total": 4}));
}

#[test]
fn distinct() {
    // Distinct cities: Paris, London, Berlin.
    let rows = run("SELECT DISTINCT c.city FROM c");
    assert_eq!(rows.len(), 3);
}

#[test]
fn undefined_property_omitted() {
    // Carol has no tags; projecting c.tags omits the property.
    let rows = run("SELECT c.tags FROM c WHERE c.id = '3'");
    assert_eq!(rows[0], json!({}));
}
