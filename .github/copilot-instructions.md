# Copilot Instructions — Azure Light Cosmos Emulator (Rust/Axum)

## Build, Test & Lint

The repository root is a Cargo **workspace**. CI runs on **stable**; the local
default toolchain may be nightly, so validate with `rustup run stable ...`.

```bash
# Build the CLI binary
cargo build -p cosmos-cli
cargo build --release -p cosmos-cli

# Run all workspace tests
cargo test --workspace

# Run one crate's tests
cargo test -p cosmos-query

# Run a single test by name
cargo test -p cosmos-query engine::tests::group_by

# Lint & format (must be clean — CI treats warnings as errors)
cargo clippy --workspace --all-targets -- -D warnings
cargo fmt --check

# Run the emulator
cargo run -p cosmos-cli -- start          # NoSQL :8081, MongoDB :10255, Explorer /explorer

# Explorer SPA (React/Vite) — Node comes from nvm, not system apt
cd explorer
npm ci
npm run dev          # dev server, proxies /dbs and /api to :8081
npm run build         # production build → crates/host/wwwroot/explorer/
npm run lint          # ESLint

# Docker (build context = repo root)
docker build -f docker/Dockerfile .
docker compose -f docker/docker-compose.yml up --build
```

## Architecture

The emulator implements the Azure Cosmos DB REST API (port 8081) and MongoDB
wire protocol (port 10255). The **default storage backend is Sqlite**
(file-backed); `InMemory` and `SurrealDb` are also selectable via `--storage`.

### Crate dependency graph

```
cli → host → nosql → core
            → storage → core
            → auth    → core
            → mongodb → core, storage, auth
            → triggers → core, storage
     query/kql → core   (used by nosql/host)
```

- **`core`** — Domain models and traits (`IDocumentStore`, `IQueryEngine`,
  `IAuthProvider`, `IChangeFeedProvider`, `IConsistencyManager`, `IActivityStore`,
  `IQueryTelemetryStore`, `IEmulatorInfoService`). Pure library, no external deps.
- **`storage`** — `IDocumentStore` implementations: **Sqlite (default)**,
  InMemory, SurrealDb; change-feed providers (LSN tracked); vector index; activity
  and query-telemetry stores.
- **`auth`** — `MasterKeyAuthProvider` (HMAC-SHA256), `EntraIdAuthProvider`
  (OIDC/JWT), `ResourceTokenProvider`, chained via a composite provider.
- **`query`** — Cosmos SQL parser/evaluator (JOIN, `GROUP BY`, spatial `ST_*`,
  `VectorDistance`, full-text/RRF, DML, explain, execution limiter).
- **`kql`** — KQL operator pipeline (where/project/extend/summarize/sort/top/
  take/count/distinct) + schema registry + monitoring adapter.
- **`nosql`** — Axum routers/handlers matching the Cosmos REST API, plus auth /
  exception / consistency middleware.
- **`mongodb`** — TCP server speaking the MongoDB wire protocol (OP_MSG/OP_QUERY).
- **`triggers`** — trigger scheduling + JS execution.
- **`host`** — Axum app assembly, middleware pipeline, background services, and
  the embedded Explorer static serving.
- **`cli`** — `cosmos-emulator` binary (`start`/`stop`/`status`/`reset`/…).
- **`parity`** — black-box parity harness + official-SDK E2E layer.

## Key Conventions

### Rust

- **Workspace at the repo root**; `Cargo.toml` declares members under `crates/*`.
  Shared dependency versions live in `[workspace.dependencies]` — add new deps
  there and opt in per-crate with `workspace = true`.
- **Keep `cargo clippy --workspace --all-targets -D warnings` clean** — CI fails
  on any warning. Keep `cargo fmt` clean.
- **Return `Undefined`/sentinel, not `null`, for invalid query inputs** — `null`
  is a valid Cosmos value.
- **Three registration surfaces must stay in sync** — the host router/run wiring
  (`crates/host/src/lib.rs`), the CLI (`crates/cli/src/main.rs`), and the test
  fixtures (`crates/parity`). A new service/option/route added to one must be
  added to the others or you get resolution errors or 404s.

### REST API pattern

Routes in `crates/nosql/` follow the Cosmos DB REST URL structure (`/dbs`,
`/dbs/{db}/colls`, `.../docs`, `.../sprocs`, `.../triggers`, `.../udfs`). All
responses include `x-ms-request-charge`, `x-ms-activity-id`, `x-ms-serviceversion`.
Errors return `{ code, message }` JSON with appropriate status codes.

### Auth header format

Master-key auth uses the Cosmos signature format:
`type=master&ver=1.0&sig={HMAC-SHA256 of "verb\nresourceType\nresourceLink\ndate\n\n"}`.
The HMAC payload is lowercased **except** `resourceLink` — name-based resource
links are **case-sensitive** and must preserve original casing. Lowercasing the
resource link is a classic parity regression that breaks signatures for any
db/container/doc whose name has uppercase characters.

The known test master key is:
`C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==`

**Local auth bypass:** the Explorer requests skip HMAC verification. Set
`x-ms-cosmos-explorer: 1` (plus `x-ms-version`) to drive the REST API from local
scripts/curl without computing signatures. Do not rely on this when testing the
auth path itself.

### Debugging point-operation 404s

Point operations look up documents by the composite key
`{db}/{container}/{partitionKeyValue}/{documentId}`. A 404 on a document you know
exists almost always means the **partition-key value mismatches** — the
`x-ms-documentdb-partitionkey` header value differs from the value extracted from
the document's PK-path field at write time. Check the container's PK path and
confirm the client sends that field's value (not the document id).

## Explorer (React)

- **Source** in [`explorer/`](../explorer/); Vite `base: '/explorer/'`,
  `outDir: '../crates/host/wwwroot/explorer'`.
- **Built assets** in `crates/host/wwwroot/explorer/` are **committed** and
  embedded into the host binary at compile time via `rust-embed`
  (`crates/host/src/explorer.rs`, `#[folder = "wwwroot/explorer"]`). Rebuild them
  with `cd explorer && npm run build` after changing the SPA; CI has a drift guard.
- Monaco Editor languages: JSON (documents), SQL (queries), JavaScript
  (sprocs/triggers/UDFs). Data via `@tanstack/react-query`. Router v7.
- **Defensive coding:** always use optional chaining (`?.`) and nullish
  coalescing (`?? default`) on API response fields — backend responses may omit
  fields or return `null`.

### Fluent UI v9 — Overlays, Dialogs & Portals

- **Never use custom backdrop/panel overlays with manual z-index for
  popover-style panels.** Fluent `Combobox`/`Dropdown`/`Select` render listboxes
  through portals at the `FluentProvider` root; a custom `position: fixed;
  z-index` backdrop sits above them and intercepts clicks. Use `Popover` +
  `PopoverSurface` + `PopoverTrigger` instead.
- **Always use `modalType="non-modal"` on `<Dialog>` with an explicit backdrop.**
  Modal dialogs disable body scroll, set `aria-hidden` on siblings, and trap
  focus — which can make content visually disappear. Add
  `<DialogSurface backdrop={{ onClick: () => setOpen(false) }}>` to restore the
  overlay and click-outside-to-close.

## Query Engine — Adding New SQL Functions

The query engine (`crates/query/`) is a hand-rolled SQL parser/evaluator.

- The expression parser supports array literals, path references (`c.field`), and
  parameters (`@param`) — but **not** inline JSON object literals. Pass GeoJSON /
  complex objects via document fields or `@parameters`, not `{...}` literals.
- To add a built-in: add a case to the built-in-function dispatch, implement the
  evaluator, return the `Undefined` sentinel for invalid inputs, add tests using
  document fields or parameters, and update `docs/` + `PARITY.md`.

### Known intentional limitations

- **Request Units**: formula-based estimation, not real metering.
- **Indexing**: most queries full-scan; **vector indexes are real** (ANN),
  `ORDER BY VectorDistance(...)` is accelerated.
- **Query materializes the whole container per call** — peak transient RSS ≈
  `concurrency × containerSize`, bounded by the query-execution limiter
  (`--max-concurrent-queries`, default `max(2, CPU/2)`). See `PERF.md`.
- **Consistency levels**: all five accepted; only Session tokens enforced
  (single-node emulator).

## Testing Patterns

- Unit tests live inline (`#[cfg(test)] mod tests`) per crate.
- **Parity tests** (`crates/parity`) boot the real host on an ephemeral socket
  and drive it with master-key–signed HTTP (black-box). The opt-in SDK layer
  (`crates/parity/sdk/`) drives the emulator with real Node/Python Azure Cosmos
  SDKs and a real MongoDB driver over HTTP and TLS.
- End each change green: `cargo test --workspace`, `cargo clippy … -D warnings`,
  `cargo fmt --check`.

## CI

`.github/workflows/ci.yml` runs fmt/clippy/test/release-build over the workspace,
a Docker image build, an Explorer SPA build with an asset-drift guard, and an
opt-in official-SDK E2E job. Path filters key off `crates/**`, `Cargo.*`,
`explorer/**`, and `crates/host/wwwroot/explorer/**`.
