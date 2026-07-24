use chrono::Duration;

use crate::ast::{BinaryOp, Expr, NamedExpression, OperatorSpec, OrderingSpec, QueryPlan, UnaryOp};
use crate::error::{KqlError, KqlResult};
use crate::evaluator::ExpressionEvaluator;
use crate::lexer::{tokenize, Token};
use crate::result::Row;
use crate::value::{convert_to_long, KqlValue};

pub(crate) fn parse_query(kql: &str) -> KqlResult<QueryPlan> {
    let segments = split_pipeline(kql);
    let table = segments
        .first()
        .map(|s| s.trim())
        .filter(|s| !s.is_empty())
        .ok_or(KqlError::EmptyQuery)?;

    let mut operators = Vec::new();
    for segment in segments.iter().skip(1) {
        let segment = segment.trim();
        if segment.is_empty() {
            continue;
        }
        operators.push(parse_operator(segment)?);
    }

    Ok(QueryPlan {
        table: table.to_string(),
        operators,
    })
}

pub(crate) fn parse_expression(input: &str) -> KqlResult<Expr> {
    let tokens = tokenize(input).map_err(KqlError::Parse)?;
    let mut parser = ExprParser { tokens, pos: 0 };
    let expr = parser.parse_expr().map_err(KqlError::Parse)?;
    parser.expect(Token::Eof).map_err(KqlError::Parse)?;
    Ok(expr)
}

fn parse_operator(segment: &str) -> KqlResult<OperatorSpec> {
    let (keyword, rest) = split_first_word(segment);
    let keyword_lower = keyword.to_ascii_lowercase();
    match keyword_lower.as_str() {
        "where" | "filter" => Ok(OperatorSpec::Where(parse_expression(rest.trim())?)),
        "project" => {
            let rest = rest.trim_start();
            if let Some(away) = strip_keyword(rest, "away") {
                Ok(OperatorSpec::ProjectAway(parse_column_list(away)))
            } else {
                Ok(OperatorSpec::Project(parse_named_expressions(rest)?))
            }
        }
        "project-away" | "project_away" => Ok(OperatorSpec::ProjectAway(parse_column_list(rest))),
        "extend" => Ok(OperatorSpec::Extend(parse_named_expressions(rest)?)),
        "summarize" => parse_summarize(rest),
        "sort" | "order" => {
            let order_text = strip_keyword(rest.trim_start(), "by").unwrap_or(rest);
            Ok(OperatorSpec::Sort(parse_orderings(order_text)))
        }
        "top" => parse_top(rest),
        "take" | "limit" => Ok(OperatorSpec::Take(parse_constant_long(rest.trim())?)),
        "count" => Ok(OperatorSpec::Count),
        "distinct" => Ok(OperatorSpec::Distinct(parse_column_list(rest))),
        other => Err(KqlError::UnsupportedOperator(other.to_string())),
    }
}

fn parse_summarize(rest: &str) -> KqlResult<OperatorSpec> {
    let (aggregate_text, by_text) = if let Some(idx) = find_top_level_keyword(rest, "by") {
        (&rest[..idx], Some(&rest[idx + 2..]))
    } else {
        (rest, None)
    };

    Ok(OperatorSpec::Summarize {
        aggregates: parse_named_expressions(aggregate_text)?,
        by: by_text
            .map(parse_named_expressions)
            .transpose()?
            .unwrap_or_default(),
    })
}

fn parse_top(rest: &str) -> KqlResult<OperatorSpec> {
    let idx = find_top_level_keyword(rest, "by")
        .ok_or_else(|| KqlError::Parse("top requires a by clause".into()))?;
    let count = parse_constant_long(rest[..idx].trim())?;
    let orderings = parse_orderings(&rest[idx + 2..]);
    Ok(OperatorSpec::Top { count, orderings })
}

fn parse_constant_long(input: &str) -> KqlResult<i64> {
    let empty = Row::new();
    let value = ExpressionEvaluator::evaluate(&parse_expression(input)?, &empty)?;
    Ok(convert_to_long(&value))
}

fn parse_named_expressions(input: &str) -> KqlResult<Vec<NamedExpression>> {
    split_top_level(input, ',')
        .into_iter()
        .filter(|part| !part.trim().is_empty())
        .map(|part| {
            let trimmed = part.trim();
            if let Some(idx) = find_named_assignment(trimmed) {
                let name = trimmed[..idx].trim().to_string();
                let expr = parse_expression(trimmed[idx + 1..].trim())?;
                return Ok(NamedExpression { name, expr });
            }

            let expr = parse_expression(trimmed)?;
            let name = infer_expression_name(trimmed, &expr);
            Ok(NamedExpression { name, expr })
        })
        .collect()
}

fn infer_expression_name(raw: &str, expr: &Expr) -> String {
    match expr {
        Expr::Identifier(name) => name.clone(),
        Expr::Call { name, .. } => {
            let name = name.to_ascii_lowercase();
            if name == "count" {
                "count_".to_string()
            } else {
                name
            }
        }
        _ => raw.trim().to_string(),
    }
}

fn parse_orderings(input: &str) -> Vec<OrderingSpec> {
    split_top_level(input, ',')
        .into_iter()
        .filter_map(|part| {
            let mut text = part.trim().to_string();
            if text.is_empty() {
                return None;
            }
            let mut ascending = false;
            if let Some(stripped) = strip_trailing_keyword(&text, "asc") {
                text = stripped.trim_end().to_string();
                ascending = true;
            } else if let Some(stripped) = strip_trailing_keyword(&text, "desc") {
                text = stripped.trim_end().to_string();
            }
            Some(OrderingSpec {
                column_name: text,
                ascending,
            })
        })
        .collect()
}

fn parse_column_list(input: &str) -> Vec<String> {
    split_top_level(input, ',')
        .into_iter()
        .map(|part| part.trim().to_string())
        .filter(|part| !part.is_empty() && is_identifier(part))
        .collect()
}

struct ExprParser {
    tokens: Vec<Token>,
    pos: usize,
}

impl ExprParser {
    fn peek(&self) -> &Token {
        &self.tokens[self.pos]
    }

    fn next(&mut self) -> Token {
        let token = self.tokens[self.pos].clone();
        if self.pos + 1 < self.tokens.len() {
            self.pos += 1;
        }
        token
    }

    fn expect(&mut self, expected: Token) -> Result<(), String> {
        if *self.peek() == expected {
            self.next();
            Ok(())
        } else {
            Err(format!("Expected {expected:?}, found {:?}", self.peek()))
        }
    }

    fn eat_word(&mut self, word: &str) -> bool {
        if matches!(self.peek(), Token::Ident(value) if value.eq_ignore_ascii_case(word)) {
            self.next();
            true
        } else {
            false
        }
    }

    fn parse_expr(&mut self) -> Result<Expr, String> {
        self.parse_or()
    }

    fn parse_or(&mut self) -> Result<Expr, String> {
        let mut left = self.parse_and()?;
        while self.eat_word("or") {
            let right = self.parse_and()?;
            left = Expr::Binary(BinaryOp::Or, Box::new(left), Box::new(right));
        }
        Ok(left)
    }

    fn parse_and(&mut self) -> Result<Expr, String> {
        let mut left = self.parse_not()?;
        while self.eat_word("and") {
            let right = self.parse_not()?;
            left = Expr::Binary(BinaryOp::And, Box::new(left), Box::new(right));
        }
        Ok(left)
    }

    fn parse_not(&mut self) -> Result<Expr, String> {
        if self.eat_word("not") {
            return Ok(Expr::Unary(UnaryOp::Not, Box::new(self.parse_not()?)));
        }
        self.parse_comparison()
    }

    fn parse_comparison(&mut self) -> Result<Expr, String> {
        let left = self.parse_additive()?;

        let negated = if self.eat_word("not") {
            true
        } else if matches!(self.peek(), Token::Bang) {
            self.next();
            true
        } else {
            false
        };

        if self.eat_word("in") {
            self.expect(Token::LParen)?;
            let mut values = Vec::new();
            if !matches!(self.peek(), Token::RParen) {
                loop {
                    values.push(self.parse_expr()?);
                    if !matches!(self.peek(), Token::Comma) {
                        break;
                    }
                    self.next();
                }
            }
            self.expect(Token::RParen)?;
            return Ok(Expr::In {
                expr: Box::new(left),
                values,
                negated,
            });
        }

        if let Some(op) = self.try_string_operator() {
            let right = self.parse_additive()?;
            let expr = Expr::Binary(op, Box::new(left), Box::new(right));
            return Ok(if negated {
                Expr::Unary(UnaryOp::Not, Box::new(expr))
            } else {
                expr
            });
        }

        if negated {
            return Err("Expected IN or a string operator after NOT/!".into());
        }

        let op = match self.peek() {
            Token::Eq => Some(BinaryOp::Eq),
            Token::Ne => Some(BinaryOp::Ne),
            Token::Lt => Some(BinaryOp::Lt),
            Token::Le => Some(BinaryOp::Le),
            Token::Gt => Some(BinaryOp::Gt),
            Token::Ge => Some(BinaryOp::Ge),
            _ => None,
        };
        if let Some(op) = op {
            self.next();
            let right = self.parse_additive()?;
            return Ok(Expr::Binary(op, Box::new(left), Box::new(right)));
        }

        Ok(left)
    }

    fn try_string_operator(&mut self) -> Option<BinaryOp> {
        let word = match self.peek() {
            Token::Ident(value) => value.to_ascii_lowercase(),
            _ => return None,
        };
        let op = match word.as_str() {
            "has" => BinaryOp::Has(true),
            "has_cs" => BinaryOp::Has(false),
            "contains" => BinaryOp::Contains(true),
            "contains_cs" => BinaryOp::Contains(false),
            "startswith" => BinaryOp::StartsWith(true),
            "startswith_cs" => BinaryOp::StartsWith(false),
            "endswith" => BinaryOp::EndsWith(true),
            "endswith_cs" => BinaryOp::EndsWith(false),
            _ => return None,
        };
        self.next();
        Some(op)
    }

    fn parse_additive(&mut self) -> Result<Expr, String> {
        let mut left = self.parse_multiplicative()?;
        loop {
            let op = match self.peek() {
                Token::Plus => BinaryOp::Add,
                Token::Minus => BinaryOp::Sub,
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
                Token::Star => BinaryOp::Mul,
                Token::Slash => BinaryOp::Div,
                Token::Percent => BinaryOp::Mod,
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
                        Token::Ident(name) => name,
                        other => return Err(format!("Expected property name, found {other:?}")),
                    };
                    expr = Expr::Member(Box::new(expr), name);
                }
                Token::LBracket => {
                    self.next();
                    let index = self.parse_expr()?;
                    self.expect(Token::RBracket)?;
                    expr = Expr::Index(Box::new(expr), Box::new(index));
                }
                _ => break,
            }
        }
        Ok(expr)
    }

    fn parse_primary(&mut self) -> Result<Expr, String> {
        match self.next() {
            Token::Number(text) => self.parse_number_literal(&text),
            Token::Str(value) => Ok(Expr::Literal(KqlValue::String(value))),
            Token::Ident(name) if name.eq_ignore_ascii_case("null") => {
                Ok(Expr::Literal(KqlValue::Null))
            }
            Token::Ident(name) if name.eq_ignore_ascii_case("true") => {
                Ok(Expr::Literal(KqlValue::Bool(true)))
            }
            Token::Ident(name) if name.eq_ignore_ascii_case("false") => {
                Ok(Expr::Literal(KqlValue::Bool(false)))
            }
            Token::Ident(name) => {
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
            Token::LParen => {
                let expr = self.parse_expr()?;
                self.expect(Token::RParen)?;
                Ok(expr)
            }
            Token::LBracket => {
                let mut values = Vec::new();
                if !matches!(self.peek(), Token::RBracket) {
                    loop {
                        values.push(self.parse_expr()?);
                        if !matches!(self.peek(), Token::Comma) {
                            break;
                        }
                        self.next();
                    }
                }
                self.expect(Token::RBracket)?;
                Ok(Expr::Call {
                    name: "__array".into(),
                    args: values,
                })
            }
            other => Err(format!("Unexpected token in expression: {other:?}")),
        }
    }

    fn parse_number_literal(&mut self, text: &str) -> Result<Expr, String> {
        if let Token::Ident(unit) = self.peek() {
            if let Some(duration) = parse_duration(text, unit) {
                self.next();
                return Ok(Expr::Literal(KqlValue::Duration(duration)));
            }
        }
        if text.contains(['.', 'e', 'E']) {
            Ok(Expr::Literal(KqlValue::Real(
                text.parse::<f64>()
                    .map_err(|_| format!("Invalid number: {text}"))?,
            )))
        } else {
            Ok(Expr::Literal(KqlValue::Long(
                text.parse::<i64>()
                    .map_err(|_| format!("Invalid number: {text}"))?,
            )))
        }
    }
}

fn parse_duration(number: &str, unit: &str) -> Option<Duration> {
    let value = number.parse::<f64>().ok()?;
    let millis = match unit.to_ascii_lowercase().as_str() {
        "ms" => value,
        "s" | "sec" | "second" | "seconds" => value * 1_000.0,
        "m" | "min" | "minute" | "minutes" => value * 60_000.0,
        "h" | "hr" | "hour" | "hours" => value * 3_600_000.0,
        "d" | "day" | "days" => value * 86_400_000.0,
        _ => return None,
    };
    Some(Duration::milliseconds(millis as i64))
}

fn split_pipeline(input: &str) -> Vec<String> {
    split_top_level(input, '|')
}

fn split_top_level(input: &str, delimiter: char) -> Vec<String> {
    let mut parts = Vec::new();
    let mut start = 0;
    let mut depth = 0i32;
    let mut quote: Option<char> = None;
    let chars: Vec<(usize, char)> = input.char_indices().collect();
    let mut i = 0;

    while i < chars.len() {
        let (idx, ch) = chars[i];
        if let Some(q) = quote {
            if ch == q {
                if i + 1 < chars.len() && chars[i + 1].1 == q {
                    i += 2;
                    continue;
                }
                quote = None;
            } else if ch == '\\' {
                i += 1;
            }
        } else {
            match ch {
                '\'' | '"' => quote = Some(ch),
                '(' | '[' | '{' => depth += 1,
                ')' | ']' | '}' => depth -= 1,
                _ if ch == delimiter && depth == 0 => {
                    parts.push(input[start..idx].to_string());
                    start = idx + ch.len_utf8();
                }
                _ => {}
            }
        }
        i += 1;
    }
    parts.push(input[start..].to_string());
    parts
}

fn split_first_word(input: &str) -> (&str, &str) {
    let trimmed = input.trim_start();
    let idx = trimmed.find(char::is_whitespace).unwrap_or(trimmed.len());
    (&trimmed[..idx], &trimmed[idx..])
}

fn strip_keyword<'a>(input: &'a str, keyword: &str) -> Option<&'a str> {
    let trimmed = input.trim_start();
    if trimmed.len() < keyword.len() || !trimmed[..keyword.len()].eq_ignore_ascii_case(keyword) {
        return None;
    }
    let boundary = trimmed[keyword.len()..]
        .chars()
        .next()
        .is_none_or(|ch| !is_word_char(ch));
    boundary.then_some(&trimmed[keyword.len()..])
}

fn strip_trailing_keyword<'a>(input: &'a str, keyword: &str) -> Option<&'a str> {
    let trimmed = input.trim_end();
    if trimmed.len() < keyword.len() {
        return None;
    }
    let start = trimmed.len() - keyword.len();
    if !trimmed[start..].eq_ignore_ascii_case(keyword) {
        return None;
    }
    let boundary = trimmed[..start]
        .chars()
        .last()
        .is_none_or(|ch| !is_word_char(ch));
    boundary.then_some(&trimmed[..start])
}

fn find_top_level_keyword(input: &str, keyword: &str) -> Option<usize> {
    let mut depth = 0i32;
    let mut quote: Option<char> = None;
    let mut iter = input.char_indices().peekable();
    while let Some((idx, ch)) = iter.next() {
        if let Some(q) = quote {
            if ch == q {
                if matches!(iter.peek(), Some((_, next)) if *next == q) {
                    iter.next();
                } else {
                    quote = None;
                }
            } else if ch == '\\' {
                iter.next();
            }
            continue;
        }

        match ch {
            '\'' | '"' => quote = Some(ch),
            '(' | '[' | '{' => depth += 1,
            ')' | ']' | '}' => depth -= 1,
            _ if depth == 0 && starts_word_at(input, idx, keyword) => return Some(idx),
            _ => {}
        }
    }
    None
}

fn find_named_assignment(input: &str) -> Option<usize> {
    let mut depth = 0i32;
    let mut quote: Option<char> = None;
    let chars: Vec<(usize, char)> = input.char_indices().collect();
    let mut i = 0;

    while i < chars.len() {
        let (idx, ch) = chars[i];
        if let Some(q) = quote {
            if ch == q {
                if i + 1 < chars.len() && chars[i + 1].1 == q {
                    i += 2;
                    continue;
                }
                quote = None;
            } else if ch == '\\' {
                i += 1;
            }
        } else {
            match ch {
                '\'' | '"' => quote = Some(ch),
                '(' | '[' | '{' => depth += 1,
                ')' | ']' | '}' => depth -= 1,
                '=' if depth == 0 => {
                    let prev = if i > 0 { Some(chars[i - 1].1) } else { None };
                    let next = if i + 1 < chars.len() {
                        Some(chars[i + 1].1)
                    } else {
                        None
                    };
                    if prev != Some('!')
                        && prev != Some('<')
                        && prev != Some('>')
                        && next != Some('=')
                        && next != Some('~')
                    {
                        return Some(idx);
                    }
                }
                _ => {}
            }
        }
        i += 1;
    }
    None
}

fn starts_word_at(input: &str, idx: usize, keyword: &str) -> bool {
    let end = idx + keyword.len();
    if end > input.len() || !input[idx..end].eq_ignore_ascii_case(keyword) {
        return false;
    }
    let before = input[..idx]
        .chars()
        .last()
        .is_none_or(|ch| !is_word_char(ch));
    let after = input[end..]
        .chars()
        .next()
        .is_none_or(|ch| !is_word_char(ch));
    before && after
}

fn is_word_char(ch: char) -> bool {
    ch.is_alphanumeric() || ch == '_' || ch == '@'
}

fn is_identifier(input: &str) -> bool {
    let mut chars = input.chars();
    matches!(chars.next(), Some(ch) if ch.is_alphabetic() || ch == '_' || ch == '@')
        && chars.all(|ch| ch.is_alphanumeric() || ch == '_' || ch == '@')
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_pipeline_around_nested_pipes() {
        assert_eq!(
            split_pipeline("T | where Message contains 'a|b' | take 1"),
            vec!["T ", " where Message contains 'a|b' ", " take 1"]
        );
    }
}
