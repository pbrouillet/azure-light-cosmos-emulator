use thiserror::Error;

pub type KqlResult<T> = Result<T, KqlError>;

#[derive(Debug, Error)]
pub enum KqlError {
    #[error("Query text is required.")]
    EmptyQuery,
    #[error("KQL parse error: {0}")]
    Parse(String),
    #[error("KQL expression error: {0}")]
    Expression(String),
    #[error("KQL operator '{0}' is not supported.")]
    UnsupportedOperator(String),
    #[error("Table '{0}' was not found.")]
    TableNotFound(String),
    #[error(transparent)]
    Other(#[from] anyhow::Error),
}
