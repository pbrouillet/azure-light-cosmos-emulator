//! Recursive-descent parser for the Cosmos SQL subset.

use serde_json::Value;

use crate::ast::*;
use crate::lexer::{tokenize, Token};

/// Parses a Cosmos SQL query string into a [`SelectStmt`].
pub fn parse(query: &str) -> Result<SelectStmt, String> {
    let tokens = tokenize(query)?;
    let mut parser = Parser { tokens, pos: 0 };
    let stmt = parser.parse_select()?;
    parser.expect(Token::Eof)?;
    Ok(stmt)
}

struct Parser {
    tokens: Vec<Token>,
    pos: usize,
}

impl Parser {
    fn peek(&self) -> &Token {
        &self.tokens[self.pos]
    }

    fn next(&mut self) -> Token {
        let t = self.tokens[self.pos].clone();
        if self.pos < self.tokens.len() - 1 {
            self.pos += 1;
        }
        t
    }

    fn expect(&mut self, tok: Token) -> Result<(), String> {
        if *self.peek() == tok {
            self.next();
            Ok(())
        } else {
            Err(format!("Expected {:?} but found {:?}", tok, self.peek()))
        }
    }

    fn is_keyword(&self, kw: &str) -> bool {
        matches!(self.peek(), Token::Keyword(k) if k == kw)
    }

    fn eat_keyword(&mut self, kw: &str) -> bool {
        if self.is_keyword(kw) {
            self.next();
            true
        } else {
            false
        }
    }

    fn parse_select(&mut self) -> Result<SelectStmt, String> {
        if !self.eat_keyword("SELECT") {
            return Err("Query must start with SELECT".into());
        }
        let distinct = self.eat_keyword("DISTINCT");

        let mut top = None;
        if self.eat_keyword("TOP") {
            top = Some(self.parse_expr()?);
        }

        let projection = self.parse_projection()?;

        // FROM (optional; default alias is `c`).
        let mut from_alias = "c".to_string();
        let mut from_in = None;
        let mut joins = Vec::new();
        if self.eat_keyword("FROM") {
            let parsed_from = self.parse_from()?;
            from_alias = parsed_from.0;
            from_in = parsed_from.1;
            joins = parsed_from.2;
        }

        let where_clause = if self.eat_keyword("WHERE") {
            Some(self.parse_expr()?)
        } else {
            None
        };

        let mut group_by = Vec::new();
        if self.eat_keyword("GROUP") {
            if !self.eat_keyword("BY") {
                return Err("Expected BY after GROUP".into());
            }
            loop {
                group_by.push(self.parse_expr()?);
                if !matches!(self.peek(), Token::Comma) {
                    break;
                }
                self.next();
            }
        }

        let mut order_by = Vec::new();
        if self.eat_keyword("ORDER") {
            if !self.eat_keyword("BY") {
                return Err("Expected BY after ORDER".into());
            }
            loop {
                let expr = self.parse_expr()?;
                let desc = if self.eat_keyword("DESC") {
                    true
                } else {
                    self.eat_keyword("ASC");
                    false
                };
                order_by.push((expr, desc));
                if !matches!(self.peek(), Token::Comma) {
                    break;
                }
                self.next();
            }
        }

        let mut offset = None;
        let mut limit = None;
        if self.eat_keyword("OFFSET") {
            offset = Some(self.parse_expr()?);
            if !self.eat_keyword("LIMIT") {
                return Err("Expected LIMIT after OFFSET".into());
            }
            limit = Some(self.parse_expr()?);
        } else if self.eat_keyword("LIMIT") {
            limit = Some(self.parse_expr()?);
        }

        Ok(SelectStmt {
            distinct,
            top,
            projection,
            from_alias,
            from_in,
            joins,
            where_clause,
            group_by,
            order_by,
            offset,
            limit,
        })
    }

    fn parse_projection(&mut self) -> Result<Projection, String> {
        if matches!(self.peek(), Token::Star) {
            self.next();
            return Ok(Projection::Star);
        }
        if self.eat_keyword("VALUE") {
            let expr = self.parse_expr()?;
            return Ok(Projection::Value(expr));
        }
        let mut items = Vec::new();
        loop {
            let expr = self.parse_expr()?;
            let alias = self.parse_optional_alias()?;
            items.push(SelectItem { expr, alias });
            if !matches!(self.peek(), Token::Comma) {
                break;
            }
            self.next();
        }
        Ok(Projection::Items(items))
    }

    fn parse_optional_alias(&mut self) -> Result<Option<String>, String> {
        if self.eat_keyword("AS") {
            match self.next() {
                Token::Ident(name) => Ok(Some(name)),
                Token::Str(name) => Ok(Some(name)),
                other => Err(format!("Expected alias name, found {other:?}")),
            }
        } else if let Token::Ident(name) = self.peek().clone() {
            // Bare alias (no AS).
            self.next();
            Ok(Some(name))
        } else {
            Ok(None)
        }
    }

    fn parse_from(&mut self) -> Result<(String, Option<Expr>, Vec<JoinClause>), String> {
        // Supports `FROM c`, `FROM root c`, `FROM alias IN source`, and
        // Cosmos self joins: `JOIN alias IN arrayExpr`.
        let first = match self.next() {
            Token::Ident(name) => name,
            other => return Err(format!("Expected FROM source, found {other:?}")),
        };
        let mut from_alias = first;
        let mut from_in = None;
        if self.is_keyword("IN") {
            self.next();
            from_in = Some(self.parse_expr()?);
        } else if let Token::Ident(alias) = self.peek().clone() {
            // Optional alias: `FROM root c`.
            self.next();
            from_alias = alias;
        }

        let mut joins = Vec::new();
        while self.eat_keyword("JOIN") {
            let alias = match self.next() {
                Token::Ident(name) => name,
                other => return Err(format!("JOIN requires an alias, found {other:?}")),
            };
            if !self.eat_keyword("IN") {
                return Err("JOIN must use the 'IN' syntax".into());
            }
            let source = self.parse_expr()?;
            joins.push(JoinClause { alias, source });
        }
        Ok((from_alias, from_in, joins))
    }

    // ---- expression precedence climbing ----

    fn parse_expr(&mut self) -> Result<Expr, String> {
        self.parse_or()
    }

    fn parse_or(&mut self) -> Result<Expr, String> {
        let mut left = self.parse_and()?;
        while self.is_keyword("OR") {
            self.next();
            let right = self.parse_and()?;
            left = Expr::Binary(BinOp::Or, Box::new(left), Box::new(right));
        }
        Ok(left)
    }

    fn parse_and(&mut self) -> Result<Expr, String> {
        let mut left = self.parse_not()?;
        while self.is_keyword("AND") {
            self.next();
            let right = self.parse_not()?;
            left = Expr::Binary(BinOp::And, Box::new(left), Box::new(right));
        }
        Ok(left)
    }

    fn parse_not(&mut self) -> Result<Expr, String> {
        if self.is_keyword("NOT") {
            self.next();
            let e = self.parse_not()?;
            return Ok(Expr::Unary(UnaryOp::Not, Box::new(e)));
        }
        self.parse_comparison()
    }

    fn parse_comparison(&mut self) -> Result<Expr, String> {
        let left = self.parse_additive()?;

        // NOT IN / NOT BETWEEN
        let negated = if self.is_keyword("NOT") {
            self.next();
            true
        } else {
            false
        };

        if self.is_keyword("BETWEEN") {
            self.next();
            let lo = self.parse_additive()?;
            if !self.eat_keyword("AND") {
                return Err("Expected AND in BETWEEN".into());
            }
            let hi = self.parse_additive()?;
            return Ok(Expr::Between {
                expr: Box::new(left),
                lo: Box::new(lo),
                hi: Box::new(hi),
                negated,
            });
        }
        if self.is_keyword("IN") {
            self.next();
            self.expect(Token::LParen)?;
            let mut items = Vec::new();
            if !matches!(self.peek(), Token::RParen) {
                loop {
                    items.push(self.parse_expr()?);
                    if !matches!(self.peek(), Token::Comma) {
                        break;
                    }
                    self.next();
                }
            }
            self.expect(Token::RParen)?;
            return Ok(Expr::In {
                expr: Box::new(left),
                items,
                negated,
            });
        }
        if negated {
            return Err("Expected IN or BETWEEN after NOT".into());
        }

        let op = match self.peek() {
            Token::Eq => Some(BinOp::Eq),
            Token::Ne => Some(BinOp::Ne),
            Token::Lt => Some(BinOp::Lt),
            Token::Le => Some(BinOp::Le),
            Token::Gt => Some(BinOp::Gt),
            Token::Ge => Some(BinOp::Ge),
            _ => None,
        };
        if let Some(op) = op {
            self.next();
            let right = self.parse_additive()?;
            return Ok(Expr::Binary(op, Box::new(left), Box::new(right)));
        }
        Ok(left)
    }

    fn parse_additive(&mut self) -> Result<Expr, String> {
        let mut left = self.parse_multiplicative()?;
        loop {
            let op = match self.peek() {
                Token::Plus => BinOp::Add,
                Token::Minus => BinOp::Sub,
                Token::Concat => BinOp::Concat,
                _ => break,
            };
            self.next();
            let right = self.parse_multiplicative()?;
            left = Expr::Binary(op, Box::new(left), Box::new(right));
        }
        Ok(left)
    }

    fn parse_multiplicative(&mut self) -> Result<Expr, String> {
        let mut left = self.parse_unary()?;
        loop {
            let op = match self.peek() {
                Token::Star => BinOp::Mul,
                Token::Slash => BinOp::Div,
                Token::Percent => BinOp::Mod,
                _ => break,
            };
            self.next();
            let right = self.parse_unary()?;
            left = Expr::Binary(op, Box::new(left), Box::new(right));
        }
        Ok(left)
    }

    fn parse_unary(&mut self) -> Result<Expr, String> {
        match self.peek() {
            Token::Minus => {
                self.next();
                Ok(Expr::Unary(UnaryOp::Neg, Box::new(self.parse_unary()?)))
            }
            Token::Plus => {
                self.next();
                Ok(Expr::Unary(UnaryOp::Pos, Box::new(self.parse_unary()?)))
            }
            _ => self.parse_postfix(),
        }
    }

    fn parse_postfix(&mut self) -> Result<Expr, String> {
        let mut expr = self.parse_primary()?;
        loop {
            match self.peek() {
                Token::Dot => {
                    self.next();
                    let name = match self.next() {
                        Token::Ident(n) => n,
                        Token::Keyword(k) => k,
                        other => return Err(format!("Expected property name, found {other:?}")),
                    };
                    expr = Expr::Member(Box::new(expr), name);
                }
                Token::LBracket => {
                    self.next();
                    let idx = self.parse_expr()?;
                    self.expect(Token::RBracket)?;
                    expr = Expr::Index(Box::new(expr), Box::new(idx));
                }
                Token::LParen => {
                    let name = call_name(&expr)
                        .ok_or_else(|| "Only identifiers can be called as functions".to_string())?;
                    self.next();
                    let mut args = Vec::new();
                    if !matches!(self.peek(), Token::RParen) {
                        loop {
                            args.push(self.parse_expr()?);
                            if !matches!(self.peek(), Token::Comma) {
                                break;
                            }
                            self.next();
                        }
                    }
                    self.expect(Token::RParen)?;
                    expr = Expr::Call { name, args };
                }
                _ => break,
            }
        }
        Ok(expr)
    }

    fn parse_primary(&mut self) -> Result<Expr, String> {
        match self.next() {
            Token::Number(n) => Ok(Expr::Lit(Value::from(n))),
            Token::Str(s) => Ok(Expr::Lit(Value::String(s))),
            Token::Param(p) => Ok(Expr::Param(p)),
            Token::Keyword(k) if k == "NULL" => Ok(Expr::Lit(Value::Null)),
            Token::Keyword(k) if k == "TRUE" => Ok(Expr::Lit(Value::Bool(true))),
            Token::Keyword(k) if k == "FALSE" => Ok(Expr::Lit(Value::Bool(false))),
            Token::LParen => {
                if self.is_keyword("SELECT") {
                    let stmt = self.parse_select()?;
                    self.expect(Token::RParen)?;
                    return Ok(Expr::Subquery(Box::new(stmt)));
                }
                let e = self.parse_expr()?;
                self.expect(Token::RParen)?;
                Ok(e)
            }
            Token::LBracket => {
                let mut items = Vec::new();
                if !matches!(self.peek(), Token::RBracket) {
                    loop {
                        items.push(self.parse_expr()?);
                        if !matches!(self.peek(), Token::Comma) {
                            break;
                        }
                        self.next();
                    }
                }
                self.expect(Token::RBracket)?;
                Ok(Expr::Array(items))
            }
            Token::LBrace => {
                let mut fields = Vec::new();
                if !matches!(self.peek(), Token::RBrace) {
                    loop {
                        let key = match self.next() {
                            Token::Str(s) => s,
                            Token::Ident(s) => s,
                            other => return Err(format!("Expected object key, found {other:?}")),
                        };
                        self.expect(Token::Colon)?;
                        let val = self.parse_expr()?;
                        fields.push((key, val));
                        if !matches!(self.peek(), Token::Comma) {
                            break;
                        }
                        self.next();
                    }
                }
                self.expect(Token::RBrace)?;
                Ok(Expr::Object(fields))
            }
            Token::Ident(name) => {
                // Function call if immediately followed by `(`.
                if matches!(self.peek(), Token::LParen) {
                    self.next();
                    let mut args = Vec::new();
                    if !matches!(self.peek(), Token::RParen) {
                        loop {
                            args.push(self.parse_expr()?);
                            if !matches!(self.peek(), Token::Comma) {
                                break;
                            }
                            self.next();
                        }
                    }
                    self.expect(Token::RParen)?;
                    Ok(Expr::Call { name, args })
                } else {
                    Ok(Expr::Identifier(name))
                }
            }
            Token::Star => Ok(Expr::Star),
            other => Err(format!("Unexpected token in expression: {other:?}")),
        }
    }
}

fn call_name(expr: &Expr) -> Option<String> {
    match expr {
        Expr::Identifier(name) => Some(name.clone()),
        Expr::Member(base, name) => call_name(base).map(|prefix| format!("{prefix}.{name}")),
        _ => None,
    }
}
