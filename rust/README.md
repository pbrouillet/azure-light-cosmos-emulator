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

## Docker

A multi-stage image builds the `cosmos-emulator` CLI and runs it in the
foreground. The build context is this `rust/` workspace directory.

```bash
cd rust

# Build and run with Docker directly
docker build -f docker/Dockerfile -t cosmos-light-emulator-rust .
docker run --rm -p 8081:8081 -p 10255:10255 -v cosmos-data:/data cosmos-light-emulator-rust

# Or with Docker Compose
docker compose -f docker/docker-compose.yml up --build
```

> The builder stage uses `rust:1-bookworm` (latest stable) because a transitive
> dependency requires the `edition2024` Cargo feature (Rust ≥ 1.85), even though
> the workspace's declared MSRV is lower.

## CI

`.github/workflows/rust-ci.yml` runs `cargo fmt --check`, `cargo clippy
--workspace --all-targets -D warnings`, `cargo test --workspace`, a release
build, and a Docker image build. It is path-filtered to `rust/**` so it runs
independently of the .NET CI workflow.

## Parity

`crates/parity` (`cargo test -p cosmos-parity`) is a black-box harness that boots
the real host on an ephemeral socket and drives it with master-key–signed HTTP,
mirroring the .NET `Parity.Tests` smoke suite (database/container/document CRUD +
auth enforcement). An opt-in official-SDK layer drives the emulator with the
real Node/Python Azure Cosmos SDKs and a real MongoDB driver over HTTP and TLS
(`crates/parity/sdk/`, runnable via `run_parity.sh --start --tls` and in the
`sdk-e2e` CI job). See [`PARITY.md`](PARITY.md) for the full feature-parity
map and remaining gaps.

## Performance

`perf/` holds load scripts (Node built-ins + a `/proc` resource sampler) that
quantify RAM/CPU under concurrent query load — the memory hotspot where the
query engine materializes the whole container per call. Run a sweep with
`perf/run_perf.sh`; see [`PERF.md`](PERF.md) for methodology, measured results,
and the `--max-concurrent-queries` RAM/throughput tradeoff.

## Status

Ported and tested (see `plan.md` for details): `core`, `storage` (InMemory /
Sqlite / SurrealDb + change feed + vector), `auth`, `query` (Cosmos SQL engine),
`nosql` (full REST surface), `triggers` (boa-based sprocs/triggers/UDFs),
`mongodb` (wire-protocol handshake), `host` (with auth + consistency enforced),
the `cosmos-emulator` `cli`, Docker/CI, and the `parity` harness. Remaining
per-crate gaps are documented in [`PARITY.md`](PARITY.md) (programmability REST
wiring + persistence, MongoDB document CRUD + host wiring of the Mongo listener,
UDF-in-query, EntraID enforcement, query JOIN/GROUP BY/spatial/vector/KQL, TLS).
