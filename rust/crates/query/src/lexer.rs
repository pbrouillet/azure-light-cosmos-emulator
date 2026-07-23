//! Tokenizer for the Cosmos SQL subset.

/// A lexical token.
#[derive(Debug, Clone, PartialEq)]
pub enum Token {
    /// Keyword (stored upper-cased) or bare identifier (original case).
    Ident(String),
    Keyword(String),
    Number(f64),
    Str(String),
    Param(String),
    // Operators / punctuation.
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Concat,
    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,
    Comma,
    Dot,
    Colon,
    Eof,
}

const KEYWORDS: &[&str] = &[
    "SELECT", "DISTINCT", "VALUE", "TOP", "FROM", "WHERE", "ORDER", "BY", "ASC", "DESC", "OFFSET",
    "LIMIT", "AND", "OR", "NOT", "IN", "BETWEEN", "AS", "NULL", "TRUE", "FALSE", "JOIN", "GROUP",
];

/// Tokenizes `input`, returning the token stream or an error message.
pub fn tokenize(input: &str) -> Result<Vec<Token>, String> {
    let chars: Vec<char> = input.chars().collect();
    let mut tokens = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        if c.is_whitespace() {
            i += 1;
            continue;
        }
        match c {
            '(' => {
                tokens.push(Token::LParen);
                i += 1;
            }
            ')' => {
                tokens.push(Token::RParen);
                i += 1;
            }
            '[' => {
                tokens.push(Token::LBracket);
                i += 1;
            }
            ']' => {
                tokens.push(Token::RBracket);
                i += 1;
            }
            '{' => {
                tokens.push(Token::LBrace);
                i += 1;
            }
            '}' => {
                tokens.push(Token::RBrace);
                i += 1;
            }
            ',' => {
                tokens.push(Token::Comma);
                i += 1;
            }
            '.' => {
                tokens.push(Token::Dot);
                i += 1;
            }
            ':' => {
                tokens.push(Token::Colon);
                i += 1;
            }
            '+' => {
                tokens.push(Token::Plus);
                i += 1;
            }
            '-' => {
                tokens.push(Token::Minus);
                i += 1;
            }
            '*' => {
                tokens.push(Token::Star);
                i += 1;
            }
            '/' => {
                tokens.push(Token::Slash);
                i += 1;
            }
            '%' => {
                tokens.push(Token::Percent);
                i += 1;
            }
            '=' => {
                tokens.push(Token::Eq);
                i += 1;
            }
            '!' => {
                if i + 1 < chars.len() && chars[i + 1] == '=' {
                    tokens.push(Token::Ne);
                    i += 2;
                } else {
                    return Err("Unexpected '!'".into());
                }
            }
            '<' => {
                if i + 1 < chars.len() && chars[i + 1] == '=' {
                    tokens.push(Token::Le);
                    i += 2;
                } else if i + 1 < chars.len() && chars[i + 1] == '>' {
                    tokens.push(Token::Ne);
                    i += 2;
                } else {
                    tokens.push(Token::Lt);
                    i += 1;
                }
            }
            '>' => {
                if i + 1 < chars.len() && chars[i + 1] == '=' {
                    tokens.push(Token::Ge);
                    i += 2;
                } else {
                    tokens.push(Token::Gt);
                    i += 1;
                }
            }
            '|' => {
                if i + 1 < chars.len() && chars[i + 1] == '|' {
                    tokens.push(Token::Concat);
                    i += 2;
                } else {
                    return Err("Unexpected '|'".into());
                }
            }
            '@' => {
                let mut j = i + 1;
                while j < chars.len() && (chars[j].is_alphanumeric() || chars[j] == '_') {
                    j += 1;
                }
                let name: String = chars[i..j].iter().collect();
                tokens.push(Token::Param(name));
                i = j;
            }
            '\'' | '"' => {
                let quote = c;
                let mut j = i + 1;
                let mut s = String::new();
                let mut closed = false;
                while j < chars.len() {
                    let ch = chars[j];
                    if ch == '\\' && j + 1 < chars.len() {
                        let esc = chars[j + 1];
                        s.push(match esc {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '\\' => '\\',
                            '\'' => '\'',
                            '"' => '"',
                            '/' => '/',
                            other => other,
                        });
                        j += 2;
                        continue;
                    }
                    if ch == quote {
                        closed = true;
                        j += 1;
                        break;
                    }
                    s.push(ch);
                    j += 1;
                }
                if !closed {
                    return Err("Unterminated string literal".into());
                }
                tokens.push(Token::Str(s));
                i = j;
            }
            c if c.is_ascii_digit() => {
                let mut j = i;
                while j < chars.len()
                    && (chars[j].is_ascii_digit()
                        || chars[j] == '.'
                        || chars[j] == 'e'
                        || chars[j] == 'E'
                        || ((chars[j] == '+' || chars[j] == '-')
                            && j > i
                            && (chars[j - 1] == 'e' || chars[j - 1] == 'E')))
                {
                    j += 1;
                }
                let text: String = chars[i..j].iter().collect();
                let n: f64 = text
                    .parse()
                    .map_err(|_| format!("Invalid number: {text}"))?;
                tokens.push(Token::Number(n));
                i = j;
            }
            c if c.is_alphabetic() || c == '_' => {
                let mut j = i;
                while j < chars.len() && (chars[j].is_alphanumeric() || chars[j] == '_') {
                    j += 1;
                }
                let word: String = chars[i..j].iter().collect();
                let upper = word.to_ascii_uppercase();
                if KEYWORDS.contains(&upper.as_str()) {
                    tokens.push(Token::Keyword(upper));
                } else {
                    tokens.push(Token::Ident(word));
                }
                i = j;
            }
            other => return Err(format!("Unexpected character: {other}")),
        }
    }
    tokens.push(Token::Eof);
    Ok(tokens)
}
