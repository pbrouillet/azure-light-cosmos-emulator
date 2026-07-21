# Rust/Axum port of Azure.Cosmos.LightEmulator

This is the work-in-progress Rust port of the emulator, living on the `rust-port`
branch alongside the original .NET solution (in the `main` worktree). See
`plan.md` in the session for the full porting roadmap.

## Workspace layout

Crates mirror the .NET project graph:

| Crate | Ports (.NET project) |
|---|---|
| `core` | `Core` — domain models + traits |
| `storage` | `Storage` — InMemory / Sqlite / SurrealDb backends, change feed, vector |
| `auth` | `Auth` — master-key HMAC, EntraID JWT, resource tokens |
| `query` | `NoSql/Query` + `Kql` — SQL & KQL engines |
| `nosql` | `NoSql` — Axum routers/handlers + middleware for the Cosmos REST API |
| `mongodb` | `MongoDB` — TCP wire-protocol server (port 10255) |
| `triggers` | `Triggers` — scheduler + JS execution |
| `host` | `Host` — Axum app assembly, middleware, static Explorer serving |
| `cli` | `Cli` — `cosmos-emulator` binary |

Dependency edges: `cli → host → nosql → core; host → storage/auth/mongodb/triggers; storage/auth/query → core`.

## Build & run

```bash
cd rust
cargo build
cargo test
cargo clippy --all-targets
cargo fmt --check

# Run the emulator (NoSQL REST API on :8081)
cargo run -p cosmos-cli -- start
curl http://localhost:8081/health
```

## Status

Scaffold only — a compiling Axum host with a `/health` endpoint and a `/dbs`
vertical slice backed by the in-memory store. Each crate contains stubs and TODO
markers filled in per the roadmap.
