# Official-SDK parity layer (opt-in)

These scripts drive a **running** Rust emulator with the real Azure Cosmos DB
SDKs, proving that unmodified official clients can speak to the emulator's port.

They are **not** part of `cargo test` — they need network access to install the
SDK packages. The always-on, self-contained parity coverage lives in the Rust
harness (`cargo test -p cosmos-parity`), which drives the real host over a socket
with master-key–signed HTTP.

## Run

```bash
# 1. Start the emulator, noting the master key it prints (or pass your own):
cargo run -p cosmos-cli -- start --key <master-key>

# 2a. Python
pip install azure-cosmos
python3 crates/parity/sdk/parity_sdk.py --endpoint http://localhost:8081 --key <master-key>

# 2b. Node
npm install @azure/cosmos
node crates/parity/sdk/parity_sdk.js --endpoint http://localhost:8081 --key <master-key>
```

Each script performs the same lifecycle as `tests/Parity.Tests/SmokeTests.cs`:
create database → create container → create/read/replace/delete document →
delete database, and prints `PARITY_SDK_<LANG>_OK` on success.

> The SDKs use **Gateway** connection mode against `http://` — the emulator does
> not serve TLS. If a future SDK version rejects `http://` endpoints, run it
> behind a local TLS terminator or use the emulator's (future) `--enable-ssl`.
