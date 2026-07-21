//! Triggers and JavaScript programmability. Ports the .NET `Triggers` project
//! (Quartz scheduling + Jint) and the sproc/UDF execution surface.
//!
//! The JS engine (`boa_engine` or `rquickjs`) provides a limited `getContext()`
//! API for pre/post triggers, stored procedures, and UDFs.

/// Kinds of programmability resources, mirroring the .NET model.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProgrammabilityKind {
    StoredProcedure,
    Trigger,
    UserDefinedFunction,
}
