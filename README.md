# Azure Light Cosmos Emulator (Rust/Axum)

A lightweight, open-source Azure Cosmos DB emulator implemented in Rust,
supporting the **NoSQL REST API** (port 8081), the **MongoDB wire protocol**
(port 10255), and an embedded web-based **Explorer** admin GUI (served at
`/explorer`).

## Features

- **NoSQL REST API** — Cosmos DB REST API compatibility on port 8081
  (databases, containers, documents, queries, sprocs/triggers/UDFs, change feed,
  offers/throughput, batch, patch).
- **MongoDB wire protocol** — native MongoDB driver support on port 10255.
- **SQL & KQL query engines** — Cosmos SQL (`SELECT`, JOIN, `GROUP BY`,
  spatial `ST_*`, `VectorDistance`, full-text) plus a KQL operator pipeline.
- **Pluggable storage** — Sqlite (default, file-backed), InMemory, and SurrealDb
  backends.
- **Auth** — master-key HMAC-SHA256, EntraID/OIDC JWT, and resource tokens.
- **TLS** — optional self-signed HTTPS via `--enable-ssl`.
- **Embedded Explorer** — React/Vite SPA compiled into the binary and served at
  `/explorer` (no separate web server required).

## Workspace layout

| Crate | Responsibility |
|---|---|
| `core` | Domain models + traits (`IDocumentStore`, `IQueryEngine`, …) |
| `storage` | InMemory / Sqlite / SurrealDb backends, change feed, vector index |
| `auth` | Master-key HMAC, EntraID JWT, resource tokens |
| `query` | Cosmos SQL engine (JOIN/GROUP BY/spatial/vector/FTS + DML/explain) |
| `kql` | KQL operator pipeline (where/project/summarize/sort/top/…) |
| `nosql` | Axum routers/handlers + middleware for the Cosmos REST API |
| `mongodb` | TCP wire-protocol server (port 10255) |
| `triggers` | Scheduler + JS execution (sprocs/triggers/UDFs) |
| `host` | Axum app assembly, middleware, embedded Explorer serving |
| `cli` | `cosmos-emulator` binary (`start`/`stop`/`status`/…) |
| `parity` | Black-box parity harness + official-SDK E2E layer |

Dependency edges: `cli → host → nosql → core; host → storage/auth/mongodb/triggers; storage/auth/query → core`.

The Explorer SPA source lives in [`explorer/`](explorer/); its built assets are
committed to `crates/host/wwwroot/explorer/` and embedded into the host binary at
compile time via `rust-embed`.

## Build & run

```bash
cargo build
cargo test --workspace
cargo clippy --workspace --all-targets
cargo fmt --check

# Run the emulator (NoSQL REST API on :8081, MongoDB on :10255)
cargo run -p cosmos-cli -- start
curl http://localhost:8081/health
# Open the Explorer admin GUI
open http://localhost:8081/explorer
```

### Explorer SPA

```bash
cd explorer
npm ci
npm run dev     # dev server (proxies /dbs and /api to :8081)
npm run build   # rebuilds committed assets in crates/host/wwwroot/explorer/
```

## Docker

A multi-stage image builds the `cosmos-emulator` CLI and runs it in the
foreground. The build context is the repository root (the Cargo workspace), so
the committed Explorer assets are embedded into the image.

```bash
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

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs `cargo fmt --check`,
`cargo clippy --workspace --all-targets -D warnings`, `cargo test --workspace`, a
release build, a Docker image build, an Explorer SPA build (with an asset-drift
guard), and an opt-in official-SDK E2E job.

## Parity

`crates/parity` (`cargo test -p cosmos-parity`) is a black-box harness that boots
the real host on an ephemeral socket and drives it with master-key–signed HTTP.
An opt-in official-SDK layer drives the emulator with the real Node/Python Azure
Cosmos SDKs and a real MongoDB driver over HTTP and TLS (`crates/parity/sdk/`,
runnable via `run_parity.sh --start --tls` and the `sdk-e2e` CI job). See
[`PARITY.md`](PARITY.md) for the full feature-parity map.

## Performance

[`perf/`](perf/) holds load scripts (Node built-ins + a `/proc` resource sampler)
that quantify RAM/CPU under concurrent query load — the memory hotspot where the
query engine materializes the whole container per call. Run a sweep with
`perf/run_perf.sh`; see [`PERF.md`](PERF.md) for methodology, measured results,
and the `--max-concurrent-queries` RAM/throughput tradeoff.

## License

MIT.
