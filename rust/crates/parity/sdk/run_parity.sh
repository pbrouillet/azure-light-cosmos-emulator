#!/usr/bin/env bash
# Parity E2E runner: drives a running (or self-started) Rust emulator with the
# real Azure Cosmos JS + Python SDKs and a real MongoDB driver, over HTTP and
# (optionally) TLS. Aggregates pass/fail and exits non-zero if any check fails.
#
# This is the opt-in "real-SDK validation" layer. It needs network access to
# install the SDK packages and is NOT part of `cargo test`.
#
# Usage:
#   # Against an already-running emulator (http):
#   crates/parity/sdk/run_parity.sh --endpoint http://localhost:8081 \
#       --mongo-uri mongodb://localhost:10255 --key <key>
#
#   # Boot the release binary itself, run everything (http + tls), tear down:
#   crates/parity/sdk/run_parity.sh --start --tls
#
# Node comes from nvm if present (this repo uses nvm, not system node).
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUST_DIR="$(cd "$SCRIPT_DIR/../../.." && pwd)"     # .../rust
REPO_DIR="$(cd "$RUST_DIR/.." && pwd)"             # .../rust-port

DEFAULT_KEY="C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="

ENDPOINT="http://localhost:8081"
MONGO_URI="mongodb://localhost:10255"
KEY="$DEFAULT_KEY"
PORT=8081
MONGO_PORT=10255
START=0
TLS=0
WORKDIR="${PARITY_WORKDIR:-$(mktemp -d)}"

while [ $# -gt 0 ]; do
  case "$1" in
    --endpoint) ENDPOINT="$2"; shift 2 ;;
    --mongo-uri) MONGO_URI="$2"; shift 2 ;;
    --key) KEY="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --mongo-port) MONGO_PORT="$2"; shift 2 ;;
    --start) START=1; shift ;;
    --tls) TLS=1; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# Load nvm-provided node if the system has none.
if ! command -v node >/dev/null 2>&1; then
  export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
  # shellcheck disable=SC1091
  [ -s "$NVM_DIR/nvm.sh" ] && . "$NVM_DIR/nvm.sh" >/dev/null 2>&1
fi

PASS=0
FAIL=0
declare -a RESULTS=()

record() { # name status
  RESULTS+=("$2 $1")
  if [ "$2" = "PASS" ]; then PASS=$((PASS+1)); else FAIL=$((FAIL+1)); fi
}

SERVER_PID=""
DATA_DIR=""
cleanup() {
  [ -n "$SERVER_PID" ] && kill "$SERVER_PID" >/dev/null 2>&1
  "$RUST_DIR/target/release/cosmos-emulator" stop >/dev/null 2>&1 || true
}
trap cleanup EXIT

boot_server() { # scheme
  local scheme="$1" extra=""
  DATA_DIR="$WORKDIR/data-$scheme"
  rm -rf "$DATA_DIR"; mkdir -p "$DATA_DIR"
  [ "$scheme" = "https" ] && extra="--enable-ssl"
  # Clear any stale single-instance state from a previous run.
  "$RUST_DIR/target/release/cosmos-emulator" stop >/dev/null 2>&1 || true
  echo ">> starting emulator ($scheme) on :$PORT / mongo :$MONGO_PORT"
  "$RUST_DIR/target/release/cosmos-emulator" start --key "$KEY" \
    --port "$PORT" --mongo-port "$MONGO_PORT" --data-dir "$DATA_DIR" $extra \
    > "$WORKDIR/server-$scheme.log" 2>&1 &
  SERVER_PID=$!
  for _ in $(seq 1 30); do
    if curl -sk -o /dev/null "$scheme://localhost:$PORT/" \
        -H "x-ms-cosmos-explorer: 1" -H "x-ms-version: 2018-12-31" 2>/dev/null; then
      return 0
    fi
    sleep 0.5
  done
  echo "!! emulator failed to become ready ($scheme)"; cat "$WORKDIR/server-$scheme.log"
  return 1
}

install_sdks() {
  echo ">> installing SDKs into $WORKDIR"
  ( cd "$WORKDIR" && npm init -y >/dev/null 2>&1 && npm install @azure/cosmos mongodb >/dev/null 2>&1 ) \
    && echo "   node SDKs installed" || echo "   !! npm install failed"
  if command -v python3 >/dev/null 2>&1 && python3 -m pip --version >/dev/null 2>&1; then
    python3 -m pip install --quiet --disable-pip-version-check azure-cosmos >/dev/null 2>&1 \
      && echo "   python azure-cosmos installed" || echo "   !! pip install azure-cosmos failed"
    PY_OK=1
  else
    echo "   (skipping python: no pip available)"
    PY_OK=0
  fi
}

run_node_cosmos() { # endpoint [env assignments...]
  local ep="$1"; shift
  echo ">> [node cosmos] $ep"
  if NODE_PATH="$WORKDIR/node_modules" env "$@" node "$SCRIPT_DIR/parity_sdk.js" \
      --endpoint "$ep" --key "$KEY" 2>&1 | sed 's/^/   /'; then
    record "node-cosmos ($ep)" PASS
  else
    record "node-cosmos ($ep)" FAIL
  fi
}

run_node_mongo() {
  echo ">> [node mongo] $MONGO_URI"
  if NODE_PATH="$WORKDIR/node_modules" node "$SCRIPT_DIR/parity_mongo.js" \
      --uri "$MONGO_URI" 2>&1 | sed 's/^/   /'; then
    record "node-mongo" PASS
  else
    record "node-mongo" FAIL
  fi
}

run_py_cosmos() { # endpoint insecure_flag
  [ "${PY_OK:-0}" = "1" ] || { echo ">> [py cosmos] skipped (no pip)"; return; }
  local ep="$1" insecure="$2"
  echo ">> [py cosmos] $ep"
  if python3 "$SCRIPT_DIR/parity_sdk.py" --endpoint "$ep" --key "$KEY" $insecure 2>&1 | sed 's/^/   /'; then
    record "py-cosmos ($ep)" PASS
  else
    record "py-cosmos ($ep)" FAIL
  fi
}

install_sdks

# ---- HTTP pass -------------------------------------------------------------
if [ "$START" = "1" ]; then
  boot_server http || exit 1
  ENDPOINT="http://localhost:$PORT"
  MONGO_URI="mongodb://localhost:$MONGO_PORT"
fi
run_node_cosmos "$ENDPOINT"
run_node_mongo
run_py_cosmos "$ENDPOINT" ""
if [ "$START" = "1" ]; then cleanup; SERVER_PID=""; sleep 1; fi

# ---- TLS pass --------------------------------------------------------------
if [ "$TLS" = "1" ] && [ "$START" = "1" ]; then
  boot_server https || exit 1
  tls_ep="https://localhost:$PORT"
  ca="$DATA_DIR/certs/localhost.pem"
  run_node_cosmos "$tls_ep" "NODE_EXTRA_CA_CERTS=$ca"
  run_py_cosmos "$tls_ep" "--insecure-tls"
  cleanup; SERVER_PID=""
fi

# ---- Summary ---------------------------------------------------------------
echo
echo "================ parity summary ================"
for r in "${RESULTS[@]}"; do echo "  $r"; done
echo "-----------------------------------------------"
echo "  PASS=$PASS FAIL=$FAIL"
echo "==============================================="
[ "$FAIL" -eq 0 ]
