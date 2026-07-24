# Parity roadmap: .NET emulator → Rust/Axum port

This document tracks feature parity between the original **.NET 10**
`Azure.Cosmos.LightEmulator` (in `../main/`) and the Rust/Axum port (this `rust/`
workspace). It also describes the parity **harness** used to verify the port.

## How parity is verified

Two complementary layers:

1. **Rust black-box harness** — `crates/parity` (`cargo test -p cosmos-parity`).
   Boots the real [`cosmos_host`] Axum app on an ephemeral TCP port and drives it
   over an actual socket with master-key–signed HTTP, mirroring
   `tests/Parity.Tests/SmokeTests.cs`. Covers: database create/read, container
   create/read (partition key), full document CRUD lifecycle with partition-key
   headers, and auth enforcement (unsigned / bad-signature → `401`). Self-
   contained; always runs in CI.
2. **Official-SDK layer** — `crates/parity/sdk/` (opt-in, needs network).
   Node `@azure/cosmos`, Python `azure-cosmos`, and a real `mongodb` driver
   drive a running emulator over **HTTP and TLS** to prove real client wire-
   compatibility. A runner (`run_parity.sh`) boots the release binary, installs
   the SDKs, and aggregates pass/fail. Not part of `cargo test`; runs in CI as a
   separate `sdk-e2e` job. See `crates/parity/sdk/README.md`.

Real-SDK validation surfaced (and fixed) three wire-compat bugs the black-box
harness missed: the `@azure/cosmos` `Item.patch` sends a **bare JSON array**
(not `{operations:[…]}`); `Items.batch` POSTs a transactional batch to
`…/colls/{coll}/docs` with `x-ms-cosmos-is-batch-request` (not the container
path); and TLS panicked on every handshake because no rustls `CryptoProvider`
was installed. All three are fixed and covered.

The per-crate suites (`cargo test --workspace`, 150 tests) additionally cover
storage backends (incl. HNSW vector index + activity/telemetry stores), auth
signing, the SQL and KQL query engines, the MongoDB wire protocol, the JS
programmability engine (persisted sprocs/triggers/UDFs + trigger hooks), the
host services (throughput, TTL, account metadata, admin config), and an
always-on **TLS** integration test (signed CRUD round-trip over `https://`).

## Component status

| .NET project | Rust crate | Status | Notes |
|---|---|---|---|
| `Core` | `cosmos-core` | ✅ Complete | Domain models, traits, consistency, ids/etags. |
| `Storage` | `cosmos-storage` | ✅ Complete | InMemory + Sqlite (default) + SurrealDb; change feed; flat vector index. |
| `Auth` | `cosmos-auth` | ✅ Complete | Master-key HMAC, resource tokens, EntraID JWT (structure-only), composite. |
| `NoSql` | `cosmos-nosql` | ✅ Complete | Full Cosmos REST surface on Axum; `x-ms-*` headers; RU costs; auth + consistency middleware; programmability routes; addresses/attachments; emulator-info (`api/emulator/*`). |
| `Kql` (SQL) | `cosmos-query` | ✅ Complete | Hand-rolled SQL engine: JOIN/`IN`, correlated subqueries, `GROUP BY`, spatial `ST_*`, `VectorDistance` (+ nearest-first `ORDER BY`), full-text/RRF/`RANK`, `udf.name(...)`, DML service, explain service, index validation, semaphore limiter. |
| `Kql` (Kusto) | `cosmos-kql` | ✅ Complete | Kusto pipeline engine: `where/project/project-away/extend/summarize/sort/top/take/count/distinct` + aggregates; served over `api/emulator/kql` against monitoring tables. |
| `Host` | `cosmos-host` | ✅ Complete | Service assembly; static Explorer; auth + consistency enforced; MongoDB listener; EntraID composite auth; throughput/RU (429) + TTL/maintenance + request-tracking middleware; TLS; `databaseAccount` (`GET /`); admin config; activity/telemetry/KQL controllers. |
| `Cli` | `cosmos-cli` | ✅ Complete | `start/stop/reset/status/export/import`; on-disk instance state; MongoDB + TLS flags wired. |
| `Triggers` | `cosmos-triggers` | ✅ Complete | `boa_engine` JS; sprocs/triggers/UDFs CRUD + execution; **persisted** records; pre/post trigger hooks; `UdfResolver` for SQL. |
| `MongoDB` | `cosmos-mongodb` | ✅ Complete | OP_MSG/OP_QUERY framing + handshake via `bson`; started by the host. |
| `Storage` (vector) | `cosmos-storage` | ✅ Complete | Real in-memory **HNSW** ANN index; activity + query-telemetry stores (InMemory/Sqlite/Surreal). |
| Docker / CI | `docker/`, `rust-ci.yml` | ✅ Complete | Multi-stage image; path-filtered CI: `check` (fmt/clippy/test/build), `docker`, `explorer-spa` (Vite build), `sdk-e2e` (real SDK/driver over http+tls). |
| `Parity.Tests` | `cosmos-parity` | ✅ Complete | Black-box harness + opt-in SDK layer (this doc). |

## Parity status (cross-cutting)

The previously-deferred follow-ups below are now **implemented and covered by
tests** (150 workspace tests green; `cargo clippy --workspace --all-targets
-D warnings` clean). Verified live end-to-end with the **real** Azure Cosmos
Node/Python SDKs and MongoDB driver over both `http://` and `https://`: full
document CRUD, upsert, parameterized/cross-partition/`SUM` queries, JSON-Patch,
transactional batch, stored-procedure execute, `GET /` account metadata, the
MongoDB handshake, and KQL over monitoring data (`activity | count`).

### Host wiring — done
- **MongoDB listener** started by `cosmos_host::run` as a background task, gated
  on `HostOptions.mongo_port`.
- **EntraID** — `CompositeAuthProvider` (master key + `EntraIdAuthProvider`)
  wired when `enable_entra` is set.
- **Throughput/RU (429), TTL cleanup / maintenance, request tracking** middleware
  and **account metadata** (`GET /` `databaseAccount`), **admin config**, and
  **activity/telemetry/KQL** controllers are all wired.

### Programmability (triggers/sprocs/UDFs) — done
- REST routes `/dbs/{db}/colls/{coll}/{sprocs|triggers|udfs}` (CRUD) + sproc
  execute; pre/post trigger hooks around document writes.
- Records **persisted** to `cosmos_sprocs`/`cosmos_triggers`/`cosmos_udfs`
  (InMemory/Sqlite/Surreal).
- **UDF-in-query** (`udf.name(...)`) resolves through a shared programmability
  engine injected into the SQL engine as a `UdfResolver`.

### Query engine (`cosmos-query`) — done
JOINs / `IN` over array subquery sources, correlated subqueries, spatial
(`ST_*`) and vector functions, `GROUP BY`, full-text / RRF / `RANK`, the DML
command service, and `QueryExplainService` are implemented. The Kusto (KQL)
dialect lives in the separate `cosmos-kql` crate.

### KQL (`cosmos-kql`) — done
Full operator pipeline ported and served over `api/emulator/kql`.

### Transport — done
- **TLS** via `--enable-ssl` (rustls + self-signed dev cert). The host installs
  the `aws-lc-rs` rustls `CryptoProvider` at startup; verified end-to-end with
  the real Node SDK over `https://` and an always-on integration test
  (`crates/parity/tests/tls.rs`). Plain `http://` remains the default; SDKs use
  Gateway mode.

### Residual notes
- Full-text search / RRF / `RANK` are functional but implemented as scoring
  **shims** — revisit if exact ranking parity with the service is required.
- HNSW is an in-memory ANN index (not persisted across restarts); the flat
  provider remains available as a compatible alias.
- **Query memory**: `SqlQueryEngine` materializes the whole container per query,
  so peak RSS scales with concurrent-query count, bounded by the
  `QueryExecutionLimiter` (`--max-concurrent-queries`, default `max(2, CPU/2)`).
  Load-validated — see [`PERF.md`](PERF.md). Streaming/paged execution is the
  recommended future refinement.

## Verifying locally

```bash
# Self-contained black-box parity (always works, no network):
cargo test -p cosmos-parity

# Whole workspace (incl. the TLS integration test):
cargo test --workspace

# Official SDK layer (needs network — see crates/parity/sdk/README.md).
# One-shot runner: boots the release binary, installs the SDKs, runs the
# Node + Python Cosmos scripts and the MongoDB driver over http *and* tls:
crates/parity/sdk/run_parity.sh --start --tls

# …or drive an already-running emulator manually:
cargo run -p cosmos-cli -- start --key <key>
python3 crates/parity/sdk/parity_sdk.py --key <key>
node   crates/parity/sdk/parity_sdk.js --key <key>
node   crates/parity/sdk/parity_mongo.js --uri mongodb://localhost:10255
```
