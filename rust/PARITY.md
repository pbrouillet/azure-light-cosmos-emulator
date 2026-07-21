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
   Node `@azure/cosmos` and Python `azure-cosmos` scripts drive a running
   emulator to prove real SDK wire-compatibility. Not part of `cargo test`.

The per-crate suites (`cargo test --workspace`, ~104 tests) additionally cover
storage backends, auth signing, the SQL query engine, the MongoDB wire protocol,
and the JS programmability engine.

## Component status

| .NET project | Rust crate | Status | Notes |
|---|---|---|---|
| `Core` | `cosmos-core` | ✅ Complete | Domain models, traits, consistency, ids/etags. |
| `Storage` | `cosmos-storage` | ✅ Complete | InMemory + Sqlite (default) + SurrealDb; change feed; flat vector index. |
| `Auth` | `cosmos-auth` | ✅ Complete | Master-key HMAC, resource tokens, EntraID JWT (structure-only), composite. |
| `NoSql` | `cosmos-nosql` | ✅ Complete | Full Cosmos REST surface on Axum; `x-ms-*` headers; RU costs; auth middleware. |
| `Kql` / query | `cosmos-query` | ✅ Core complete | Hand-rolled SQL engine (see gaps below). |
| `Host` | `cosmos-host` | ✅ Complete | Service assembly; static Explorer; **auth + consistency now enforced**. |
| `Cli` | `cosmos-cli` | ✅ Complete | `start/stop/reset/status/export/import`; on-disk instance state. |
| `Triggers` | `cosmos-triggers` | ✅ Engine complete | `boa_engine` JS; sprocs/triggers/UDFs CRUD + execution (see gaps). |
| `MongoDB` | `cosmos-mongodb` | ✅ Wire complete | OP_MSG/OP_QUERY framing + handshake via `bson` (see gaps). |
| Docker / CI | `docker/`, `rust-ci.yml` | ✅ Complete | Multi-stage image; path-filtered CI. |
| `Parity.Tests` | `cosmos-parity` | ✅ Complete | Black-box harness + opt-in SDK layer (this doc). |

## Remaining parity gaps (cross-cutting)

These are documented, intentional follow-ups — none block the core REST/CRUD
contract exercised by the harness.

### Host wiring
- **MongoDB listener not started by the host.** `cosmos-mongodb` is fully
  functional and unit/integration-tested, but `cosmos_host::run` does not yet
  spawn the TCP listener; the CLI records `--mongo-port` only for the banner.
  *Next:* start `MongoDbServer::bind(mongo_port).run()` as a background task in
  `run()`, gated on a `HostOptions.mongo_port`.
- **EntraID enforcement.** `--enable-entra` is recorded but the host wires only
  `MasterKeyAuthProvider`. *Next:* build a `CompositeAuthProvider` (master key +
  `EntraIdAuthProvider`) when `enable_entra` is set.

### Programmability (triggers/sprocs/UDFs)
- **No REST wiring.** `JsProgrammabilityEngine` is standalone (like auth was);
  the `ProgrammabilityController` routes, an `AppState` field, and pre/post
  trigger hooks in `documents.rs` are not yet added. *Next:* add
  `/dbs/{db}/colls/{coll}/{sprocs|triggers|udfs}` routes + execute endpoint;
  invoke pre/post triggers around document writes.
- **Records are in-memory**, not persisted to `cosmos_sprocs`/`cosmos_triggers`/
  `cosmos_udfs` tables — lost on restart.
- **UDF-in-query** (`udf.name(...)` inside SQL) is unsupported by the query
  engine.

### Query engine (`cosmos-query`)
Deferred features: JOINs / `IN` over array subquery sources, correlated
subqueries, spatial (`ST_*`) and vector functions, `GROUP BY`, full-text / RRF,
the DML command service, `QueryExplainService`, and KQL.

### Transport
- **No TLS.** The emulator serves `http://` only (`--enable-ssl` is a no-op).
  SDKs must use Gateway mode against the http endpoint.

## Verifying locally

```bash
# Self-contained black-box parity (always works, no network):
cargo test -p cosmos-parity

# Whole workspace:
cargo test --workspace

# Official SDK layer (needs network — see crates/parity/sdk/README.md):
cargo run -p cosmos-cli -- start --key <key>
python3 crates/parity/sdk/parity_sdk.py --key <key>
node   crates/parity/sdk/parity_sdk.js --key <key>
```
