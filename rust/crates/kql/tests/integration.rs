use cosmos_kql::{execute_query, KqlError, KqlQueryResult, Row};
use serde_json::{json, Value};

fn as_row(value: Value) -> Row {
    value
        .as_object()
        .expect("test row must be an object")
        .clone()
}

fn events() -> Vec<Row> {
    vec![
        as_row(json!({"Name":"a","Level":"error","Message":"Disk Full","Value":5,"User":"u1"})),
        as_row(json!({"Name":"b","Level":"info","Message":"Started","Value":3,"User":"u2"})),
        as_row(json!({"Name":"c","Level":"error","Message":"disk slow","Value":7,"User":"u1"})),
        as_row(json!({"Name":"d","Level":"warn","Message":"Network","Value":1,"User":null})),
    ]
}

fn run(query: &str) -> KqlQueryResult {
    let rows = events();
    execute_query(query, |table| {
        if table.eq_ignore_ascii_case("Events") {
            Ok(rows.clone())
        } else {
            Err(KqlError::TableNotFound(table.to_string()))
        }
    })
    .unwrap()
}

#[test]
fn where_extend_and_project_evaluate_expressions() {
    let result = run(
        "Events | where Level == 'error' and Message contains 'disk' \
         | extend MessageUpper=toupper(Message), Twice=Value * 2 \
         | project Name, MessageUpper, Twice",
    );

    assert_eq!(result.rows.len(), 2);
    assert_eq!(result.rows[0]["Name"], json!("a"));
    assert_eq!(result.rows[0]["MessageUpper"], json!("DISK FULL"));
    assert_eq!(result.rows[0]["Twice"], json!(10));
    assert_eq!(result.rows[1]["Name"], json!("c"));
}

#[test]
fn project_away_removes_columns_case_insensitively() {
    let result = run("Events | take 1 | project-away message, User");

    assert_eq!(result.rows.len(), 1);
    assert!(result.rows[0].get("Message").is_none());
    assert!(result.rows[0].get("User").is_none());
    assert_eq!(result.rows[0]["Name"], json!("a"));
}

#[test]
fn summarize_without_grouping_computes_aggregates() {
    let result = run(
        "Events | summarize count(), Total=sum(Value), Avg=avg(Value), \
         Min=min(Value), Max=max(Value), Users=dcount(User)",
    );

    let row = &result.rows[0];
    assert_eq!(row["count_"], json!(4));
    assert_eq!(row["Total"], json!(16.0));
    assert_eq!(row["Avg"], json!(4.0));
    assert_eq!(row["Min"], json!(1));
    assert_eq!(row["Max"], json!(7));
    assert_eq!(row["Users"], json!(3));
}

#[test]
fn summarize_by_grouping_and_conditional_aggregates() {
    let result = run(
        "Events | summarize Items=count(), Errors=countif(Level == 'error'), \
         ErrorValue=sumif(Value, Level == 'error'), AnyUser=take_any(User) by Level | sort by Level asc",
    );

    assert_eq!(result.rows.len(), 3);
    assert_eq!(result.rows[0]["Level"], json!("error"));
    assert_eq!(result.rows[0]["Items"], json!(2));
    assert_eq!(result.rows[0]["Errors"], json!(2));
    assert_eq!(result.rows[0]["ErrorValue"], json!(12.0));
    assert_eq!(result.rows[0]["AnyUser"], json!("u1"));
    assert_eq!(result.rows[1]["Level"], json!("info"));
    assert_eq!(result.rows[2]["Level"], json!("warn"));
}

#[test]
fn sort_take_top_count_and_distinct_match_pipeline_semantics() {
    let sorted = run("Events | sort by Value asc | take 2 | project Name, Value");
    assert_eq!(sorted.rows[0]["Name"], json!("d"));
    assert_eq!(sorted.rows[1]["Name"], json!("b"));

    let top = run("Events | top 2 by Value desc | project Name, Value");
    assert_eq!(top.rows[0]["Name"], json!("c"));
    assert_eq!(top.rows[1]["Name"], json!("a"));

    let count = run("Events | where Value >= 3 | count");
    assert_eq!(count.rows[0]["Count"], json!(3));

    let distinct = run("Events | distinct Level | sort by Level asc");
    assert_eq!(
        distinct
            .rows
            .iter()
            .map(|r| &r["Level"])
            .collect::<Vec<_>>(),
        vec![&json!("error"), &json!("info"), &json!("warn")]
    );
}

#[test]
fn scalar_functions_and_in_operator_are_supported() {
    let result = run(
        "Events | where Level in ('error', 'warn') and Message !contains_cs 'Disk' \
         | extend Label=strcat(tolower(Level), ':', substring(Name, 0, 1)), \
                  Pick=iff(isnotempty(User), User, 'none') \
         | project Name, Label, Pick | sort by Name asc",
    );

    assert_eq!(result.rows.len(), 2);
    assert_eq!(result.rows[0]["Name"], json!("c"));
    assert_eq!(result.rows[0]["Label"], json!("error:c"));
    assert_eq!(result.rows[0]["Pick"], json!("u1"));
    assert_eq!(result.rows[1]["Name"], json!("d"));
    assert_eq!(result.rows[1]["Pick"], json!("none"));
}

#[test]
fn empty_summarize_still_returns_single_count_row() {
    let result = run(
        "Events | where Level == 'missing' | summarize count(), Total=sum(Value), Avg=avg(Value)",
    );

    assert_eq!(result.rows.len(), 1);
    assert_eq!(result.rows[0]["count_"], json!(0));
    assert_eq!(result.rows[0]["Total"], json!(0.0));
    assert_eq!(result.rows[0]["Avg"], Value::Null);
}
