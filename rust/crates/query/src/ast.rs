//! Abstract syntax tree for the Cosmos SQL subset.
//!
//! Ports the query-plan structures used by `CosmosQueryEngine` (SELECT with
//! projections, FROM alias, WHERE, ORDER BY, OFFSET/LIMIT, TOP, DISTINCT).

use serde_json::Value;

/// Binary operators.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BinOp {
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
    Concat,
}

/// Unary operators.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UnaryOp {
    Not,
    Neg,
    Pos,
}

/// A scalar expression.
#[derive(Debug, Clone)]
pub enum Expr {
    /// A JSON literal (`null`, boolean, number, string).
    Lit(Value),
    /// An `@parameter` reference (key includes the leading `@`).
    Param(String),
    /// A bare identifier (typically the FROM alias / root document).
    Identifier(String),
    /// Property access (`base.name`).
    Member(Box<Expr>, String),
    /// Index access (`base[index]`).
    Index(Box<Expr>, Box<Expr>),
    Unary(UnaryOp, Box<Expr>),
    Binary(BinOp, Box<Expr>, Box<Expr>),
    /// `expr [NOT] BETWEEN lo AND hi`.
    Between {
        expr: Box<Expr>,
        lo: Box<Expr>,
        hi: Box<Expr>,
        negated: bool,
    },
    /// `expr [NOT] IN (a, b, ...)`.
    In {
        expr: Box<Expr>,
        items: Vec<Expr>,
        negated: bool,
    },
    /// A function or aggregate call.
    Call {
        name: String,
        args: Vec<Expr>,
    },
    /// An array literal `[a, b, ...]`.
    Array(Vec<Expr>),
    /// An object literal `{ "k": v, ... }`.
    Object(Vec<(String, Expr)>),
}

/// A single `SELECT` output item.
#[derive(Debug, Clone)]
pub struct SelectItem {
    pub expr: Expr,
    pub alias: Option<String>,
}

/// The projection mode.
#[derive(Debug, Clone)]
pub enum Projection {
    /// `SELECT *`.
    Star,
    /// `SELECT VALUE <expr>`.
    Value(Expr),
    /// `SELECT a, b AS c, ...`.
    Items(Vec<SelectItem>),
}

/// A parsed `SELECT` statement.
#[derive(Debug, Clone)]
pub struct SelectStmt {
    pub distinct: bool,
    pub top: Option<Expr>,
    pub projection: Projection,
    /// Root alias bound to each document (defaults to `c` / the FROM source).
    pub from_alias: String,
    pub where_clause: Option<Expr>,
    /// Sort keys with a descending flag.
    pub order_by: Vec<(Expr, bool)>,
    pub offset: Option<Expr>,
    pub limit: Option<Expr>,
}
