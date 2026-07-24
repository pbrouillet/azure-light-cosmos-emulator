//! Integration tests for the Cosmos SQL query engine.

use std::collections::HashMap;

use cosmos_query::{run_query, run_query_with_udf_resolver, UdfResolver};
use serde_json::{json, Value};

fn docs() -> Vec<Value> {
    vec![
        json!({"id": "1", "name": "Alice", "age": 30, "city": "Paris", "tags": ["a", "b"], "scores": [1, 2], "embedding": [1, 0, 0], "text": "quick brown fox"}),
        json!({"id": "2", "name": "Bob", "age": 25, "city": "London", "tags": ["b", "c"], "scores": [3], "embedding": [0, 1, 0], "text": "hello world"}),
        json!({"id": "3", "name": "Carol", "age": 35, "city": "Paris"}),
        json!({"id": "4", "name": "Dave", "age": 25, "city": "Berlin", "tags": [], "embedding": [0.9, 0.1, 0]}),
    ]
}

fn run(q: &str) -> Vec<Value> {
    run_query(q, &docs(), &HashMap::new()).expect("query should succeed")
}

fn run_p(q: &str, params: HashMap<String, Value>) -> Vec<Value> {
    run_query(q, &docs(), &params).expect("query should succeed")
}

struct TestUdfResolver;

impl UdfResolver for TestUdfResolver {
    fn eval(
        &self,
        _database_id: &str,
        _container_id: &str,
        name: &str,
        args: &[Value],
    ) -> Option<Value> {
        match name.to_ascii_lowercase().as_str() {
            "double" => args.first().and_then(Value::as_f64).map(|n| json!(n * 2.0)),
            "citylabel" => args
                .first()
                .and_then(Value::as_str)
                .map(|city| json!(format!("city:{city}"))),
            "isparis" => args
                .first()
                .and_then(Value::as_str)
                .map(|city| json!(city == "Paris")),
            _ => None,
        }
    }
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

#[test]
fn join_expands_array_items() {
    let rows = run("SELECT c.id, t AS tag FROM c JOIN t IN c.tags ORDER BY c.id, t");
    assert_eq!(
        rows,
        vec![
            json!({"id": "1", "tag": "a"}),
            json!({"id": "1", "tag": "b"}),
            json!({"id": "2", "tag": "b"}),
            json!({"id": "2", "tag": "c"}),
        ]
    );
}

#[test]
fn from_in_iterates_arrays() {
    let rows = run("SELECT VALUE t FROM t IN c.tags ORDER BY t");
    let vals: Vec<Value> = rows.into_iter().map(|r| r["$1"].clone()).collect();
    assert_eq!(vals, vec![json!("a"), json!("b"), json!("b"), json!("c")]);
}

#[test]
fn correlated_subquery_counts_array_items() {
    let rows = run(
        "SELECT c.id, (SELECT VALUE COUNT(1) FROM t IN c.tags) AS tagCount FROM c ORDER BY c.id",
    );
    assert_eq!(rows[0], json!({"id": "1", "tagCount": 2}));
    assert_eq!(rows[1], json!({"id": "2", "tagCount": 2}));
    assert_eq!(rows[2], json!({"id": "3"}));
    assert_eq!(rows[3], json!({"id": "4", "tagCount": 0}));
}

#[test]
fn group_by_with_aggregates() {
    let rows = run("SELECT c.city AS city, COUNT(1) AS count, AVG(c.age) AS avgAge FROM c GROUP BY c.city ORDER BY c.city");
    assert_eq!(
        rows,
        vec![
            json!({"city": "Berlin", "count": 1, "avgAge": 25}),
            json!({"city": "London", "count": 1, "avgAge": 25}),
            json!({"city": "Paris", "count": 2, "avgAge": 32.5}),
        ]
    );
}

#[test]
fn spatial_functions_use_geojson_fields_and_parameters() {
    let docs = vec![json!({
        "id": "geo",
        "location": {"type": "Point", "coordinates": [2.3522, 48.8566]},
        "near": {"type": "Point", "coordinates": [2.3622, 48.8566]},
        "box": {"type": "Polygon", "coordinates": [[[2.0, 48.0], [3.0, 48.0], [3.0, 49.0], [2.0, 49.0], [2.0, 48.0]]]}
    })];
    let rows = run_query(
        "SELECT ST_DISTANCE(c.location, c.near) AS d, ST_WITHIN(c.location, c.box) AS inside, ST_ISVALID(c.box) AS valid FROM c",
        &docs,
        &HashMap::new(),
    ).unwrap();
    assert!(rows[0]["d"].as_f64().unwrap() > 700.0);
    assert_eq!(rows[0]["inside"], json!(true));
    assert_eq!(rows[0]["valid"], json!(true));

    let mut params = HashMap::new();
    params.insert(
        "@target".to_string(),
        json!({"type": "Point", "coordinates": [2.3522, 48.8566]}),
    );
    let rows = run_query(
        "SELECT VALUE ST_DISTANCE(c.location, @target) FROM c",
        &docs,
        &params,
    )
    .unwrap();
    assert_eq!(rows[0]["$1"], json!(0));
}

#[test]
fn vector_distance_scores_and_orders_nearest_first() {
    let rows = run("SELECT TOP 2 c.id, VectorDistance(c.embedding, [1, 0, 0]) AS score FROM c ORDER BY VectorDistance(c.embedding, [1, 0, 0])");
    let ids: Vec<&str> = rows.iter().map(|r| r["id"].as_str().unwrap()).collect();
    assert_eq!(ids, vec!["1", "4"]);
    assert_eq!(rows[0]["score"], json!(1));
}

#[test]
fn vector_distance_supports_euclidean_options() {
    let rows = run("SELECT VALUE VectorDistance(c.embedding, [0, 0, 0], false, {\"distanceFunction\":\"euclidean\"}) FROM c WHERE c.id = '1'");
    assert_eq!(rows[0]["$1"], json!(1));
}

#[test]
fn full_text_functions_match_terms_case_insensitively() {
    let rows = run("SELECT VALUE FullTextContains(c.text, 'QUICK') FROM c WHERE c.id = '1'");
    assert_eq!(rows[0]["$1"], json!(true));

    let rows = run(
        "SELECT VALUE FullTextScore(c.text, 'quick', 'brown', 'missing') FROM c WHERE c.id = '1'",
    );
    assert_eq!(rows[0]["$1"], json!(2));

    let rows = run("SELECT c.id FROM c WHERE FullTextContainsAny(c.text, 'world', 'missing')");
    assert_eq!(rows, vec![json!({"id": "2"})]);
}

#[test]
fn udf_calls_project_and_filter_with_resolver() {
    let resolver = TestUdfResolver;
    let rows = run_query_with_udf_resolver(
        "SELECT c.name, udf.double(c.age) AS doubled, udf.cityLabel(c.city) AS label FROM c WHERE udf.isParis(c.city) ORDER BY c.id",
        &docs(),
        &HashMap::new(),
        Some(&resolver),
    )
    .expect("query should succeed");

    assert_eq!(
        rows,
        vec![
            json!({"name": "Alice", "doubled": 60.0, "label": "city:Paris"}),
            json!({"name": "Carol", "doubled": 70.0, "label": "city:Paris"}),
        ]
    );
}

#[test]
fn missing_udf_returns_undefined() {
    let resolver = TestUdfResolver;
    let rows = run_query_with_udf_resolver(
        "SELECT c.name, udf.missing(c.age) AS missing FROM c WHERE c.id = '1'",
        &docs(),
        &HashMap::new(),
        Some(&resolver),
    )
    .expect("query should succeed");

    assert_eq!(rows, vec![json!({"name": "Alice"})]);
}
