# Official-SDK parity layer (opt-in)

These scripts drive a **running** Rust emulator with the real Azure Cosmos DB
SDKs and a real MongoDB driver, proving that unmodified official clients can
speak to the emulator over both `http://` and `https://`.

They are **not** part of `cargo test` — they need network access to install the
client packages. The always-on, self-contained parity coverage lives in the Rust
harness (`cargo test -p cosmos-parity`, plus the `tests/tls.rs` TLS integration
test), which drives the real host over a socket with master-key–signed HTTP(S).

## Scripts

| File | Client | Coverage |
|---|---|---|
| `parity_sdk.js` | Node `@azure/cosmos` | db/container/doc CRUD, upsert, parameterized + cross-partition + `SUM` query, JSON-Patch, transactional batch, sproc execute. Prints `PARITY_SDK_NODE_OK`. |
| `parity_sdk.py` | Python `azure-cosmos` | same lifecycle as the Node script (`--insecure-tls` to accept the dev cert). Prints `PARITY_SDK_PYTHON_OK`. |
| `parity_mongo.js` | Node `mongodb` driver | connect/handshake, `ping`, `buildInfo`, `hello`. Prints `PARITY_SDK_MONGO_OK`. (Document CRUD over the Mongo wire is a scaffold in both .NET and Rust and is intentionally out of scope.) |
| `run_parity.sh` | — | Runner: boots the release binary, installs the SDKs, runs all of the above over HTTP and TLS, and aggregates pass/fail. |

## Run

### One-shot runner (recommended)

```bash
# Boots the release cosmos-emulator, runs Node + Python Cosmos + Mongo over
# http, then again over https (--tls), and tears everything down:
crates/parity/sdk/run_parity.sh --start --tls

# Or point it at an already-running emulator:
crates/parity/sdk/run_parity.sh \
    --endpoint http://localhost:8081 \
    --mongo-uri mongodb://localhost:10255 --key <master-key>
```

Node comes from `nvm` if the system has none (this repo uses nvm). Python is
auto-skipped when `pip` is unavailable (e.g. locally); CI installs it via
`actions/setup-python`.

### Manual

```bash
# 1. Start the emulator, noting the master key it prints (or pass your own):
cargo run -p cosmos-cli -- start --key <master-key>          # add --enable-ssl for TLS

# 2a. Node
npm install @azure/cosmos mongodb
node crates/parity/sdk/parity_sdk.js   --endpoint http://localhost:8081 --key <master-key>
node crates/parity/sdk/parity_mongo.js --uri mongodb://localhost:10255

# 2b. Python
pip install azure-cosmos
python3 crates/parity/sdk/parity_sdk.py --endpoint http://localhost:8081 --key <master-key>
```

## TLS

TLS is fully working: `--enable-ssl` serves rustls with a self-signed dev cert
written under `<data-dir>/certs/localhost.pem`. Point the client's CA trust at
it (Node: `NODE_EXTRA_CA_CERTS=<pem>`; Python: `--insecure-tls`). The runner does
this automatically in its `--tls` pass.

## CI

The `sdk-e2e` job in `.github/workflows/rust-ci.yml` runs `run_parity.sh
--start --tls` on every push/PR that touches the Rust workspace, exercising the
Node + Python Cosmos SDKs and the MongoDB driver over both http and https.
