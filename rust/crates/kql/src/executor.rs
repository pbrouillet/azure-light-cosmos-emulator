use crate::ast::OperatorSpec;
use crate::error::{KqlError, KqlResult};
use crate::operators::{
    CountOp, DistinctOp, ExtendOp, KqlOperator, ProjectAwayOp, ProjectOp, SortOp, SummarizeOp,
    TakeOp, TopOp, WhereOp,
};
use crate::parser::parse_query;
use crate::result::{KqlQueryResult, Row};
use crate::schema::{infer_schema, KqlSchemaRegistry, KqlTableSchema};

#[derive(Debug, Clone)]
pub struct KqlQueryExecutor {
    schema_registry: KqlSchemaRegistry,
}

impl KqlQueryExecutor {
    pub fn new(schema_registry: KqlSchemaRegistry) -> Self {
        Self { schema_registry }
    }

    pub fn execute<F>(&self, kql: &str, table_resolver: F) -> KqlResult<KqlQueryResult>
    where
        F: Fn(&str) -> KqlResult<Vec<Row>>,
    {
        if kql.trim().is_empty() {
            return Err(KqlError::EmptyQuery);
        }

        let plan = parse_query(kql)?;
        let source_schema = self.schema_registry.get_table(&plan.table);
        let mut rows = table_resolver(&plan.table)?;

        for op_spec in plan.operators {
            let operator = create_operator(op_spec);
            rows = operator.execute(rows)?;
        }

        let schema = if rows.is_empty() {
            source_schema
                .filter(|_| kql.trim().eq_ignore_ascii_case(&plan.table))
                .unwrap_or_else(|| KqlTableSchema::new("result", Vec::new()))
        } else {
            infer_schema(&rows)
        };

        Ok(KqlQueryResult::new(schema, rows))
    }
}

fn create_operator(spec: OperatorSpec) -> Box<dyn KqlOperator> {
    match spec {
        OperatorSpec::Where(predicate) => Box::new(WhereOp::new(predicate)),
        OperatorSpec::Project(columns) => Box::new(ProjectOp::new(columns)),
        OperatorSpec::ProjectAway(columns) => Box::new(ProjectAwayOp::new(columns)),
        OperatorSpec::Extend(columns) => Box::new(ExtendOp::new(columns)),
        OperatorSpec::Summarize { aggregates, by } => Box::new(SummarizeOp::new(aggregates, by)),
        OperatorSpec::Sort(orderings) => Box::new(SortOp::new(orderings)),
        OperatorSpec::Top { count, orderings } => Box::new(TopOp::new(count, orderings)),
        OperatorSpec::Take(count) => Box::new(TakeOp::new(count)),
        OperatorSpec::Count => Box::new(CountOp),
        OperatorSpec::Distinct(columns) => Box::new(DistinctOp::new(columns)),
    }
}
