use crate::value::KqlValue;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum BinaryOp {
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    And,
    Or,
    Has(bool),
    Contains(bool),
    StartsWith(bool),
    EndsWith(bool),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum UnaryOp {
    Not,
    Neg,
    Pos,
}

#[derive(Debug, Clone)]
pub(crate) enum Expr {
    Literal(KqlValue),
    Identifier(String),
    Member(Box<Expr>, String),
    Index(Box<Expr>, Box<Expr>),
    Unary(UnaryOp, Box<Expr>),
    Binary(BinaryOp, Box<Expr>, Box<Expr>),
    In {
        expr: Box<Expr>,
        values: Vec<Expr>,
        negated: bool,
    },
    Call {
        name: String,
        args: Vec<Expr>,
    },
}

#[derive(Debug, Clone)]
pub(crate) struct NamedExpression {
    pub name: String,
    pub expr: Expr,
}

#[derive(Debug, Clone)]
pub(crate) struct OrderingSpec {
    pub column_name: String,
    pub ascending: bool,
}

#[derive(Debug, Clone)]
pub(crate) enum OperatorSpec {
    Where(Expr),
    Project(Vec<NamedExpression>),
    ProjectAway(Vec<String>),
    Extend(Vec<NamedExpression>),
    Summarize {
        aggregates: Vec<NamedExpression>,
        by: Vec<NamedExpression>,
    },
    Sort(Vec<OrderingSpec>),
    Top {
        count: i64,
        orderings: Vec<OrderingSpec>,
    },
    Take(i64),
    Count,
    Distinct(Vec<String>),
}

#[derive(Debug, Clone)]
pub(crate) struct QueryPlan {
    pub table: String,
    pub operators: Vec<OperatorSpec>,
}
