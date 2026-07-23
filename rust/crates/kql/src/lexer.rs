#[derive(Debug, Clone, PartialEq)]
pub(crate) enum Token {
    Ident(String),
    Number(String),
    Str(String),
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
    Bang,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Comma,
    Dot,
    Eof,
}

pub(crate) fn tokenize(input: &str) -> Result<Vec<Token>, String> {
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
            ',' => {
                tokens.push(Token::Comma);
                i += 1;
            }
            '.' => {
                tokens.push(Token::Dot);
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
                i += if i + 1 < chars.len() && (chars[i + 1] == '=' || chars[i + 1] == '~') {
                    2
                } else {
                    1
                };
            }
            '!' => {
                if i + 1 < chars.len() && (chars[i + 1] == '=' || chars[i + 1] == '~') {
                    tokens.push(Token::Ne);
                    i += 2;
                } else {
                    tokens.push(Token::Bang);
                    i += 1;
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
            '\'' | '"' => {
                let quote = c;
                let mut j = i + 1;
                let mut value = String::new();
                let mut closed = false;
                while j < chars.len() {
                    let ch = chars[j];
                    if ch == '\\' && j + 1 < chars.len() {
                        value.push(match chars[j + 1] {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '\\' => '\\',
                            '\'' => '\'',
                            '"' => '"',
                            other => other,
                        });
                        j += 2;
                        continue;
                    }
                    if ch == quote {
                        if j + 1 < chars.len() && chars[j + 1] == quote {
                            value.push(quote);
                            j += 2;
                            continue;
                        }
                        closed = true;
                        j += 1;
                        break;
                    }
                    value.push(ch);
                    j += 1;
                }
                if !closed {
                    return Err("Unterminated string literal".into());
                }
                tokens.push(Token::Str(value));
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
                tokens.push(Token::Number(chars[i..j].iter().collect()));
                i = j;
            }
            c if c.is_alphabetic() || c == '_' || c == '@' => {
                let mut j = i;
                while j < chars.len()
                    && (chars[j].is_alphanumeric() || chars[j] == '_' || chars[j] == '@')
                {
                    j += 1;
                }
                tokens.push(Token::Ident(chars[i..j].iter().collect()));
                i = j;
            }
            other => return Err(format!("Unexpected character: {other}")),
        }
    }

    tokens.push(Token::Eof);
    Ok(tokens)
}
