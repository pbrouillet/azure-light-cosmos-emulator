#!/usr/bin/env bash
# Perf & load orchestrator for the Rust Cosmos emulator.
#
# Focus: the query memory hotspot. SqlQueryEngine materializes the entire
# container per query, bounded by QueryExecutionLimiter. This driver boots the
# release emulator with a given storage backend and --max-concurrent-queries L,
# seeds one large container, then drives a fixed number of concurrent full-scan
# queries. By sweeping L (and the backend) it shows that peak RSS is bounded by
# the limiter, and how throughput/latency/CPU scale with it.
#
# Usage:
#   rust/perf/run_perf.sh                     # default sweep, sqlite + in-memory
#   rust/perf/run_perf.sh --docs 40000 --doc-size 2048 --duration 25 \
#       --backends "sqlite" --limiters "1 4 16 64" --driver-concurrency 64
#
# Requires a release build of cosmos-cli (built automatically if missing) and
# Node (from nvm if the system has none). No npm install needed.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUST_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"                 # .../rust
BIN="$RUST_DIR/target/release/cosmos-emulator"
DRIVER="$SCRIPT_DIR/query_load.js"
SAMPLER="$SCRIPT_DIR/sample_resources.sh"

KEY="C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
PORT=8081
MONGO_PORT=10255
DOCS=25000
DOC_SIZE=1024
DURATION=20
BACKENDS="sqlite in-memory"
LIMITERS="1 4 16 64"
DRIVER_CONCURRENCY=64
QUERY="SELECT c.id, c.value FROM c WHERE c.value > 500"
WORKDIR="${PERF_WORKDIR:-$(mktemp -d)}"
INTERVAL="0.2"

while [ $# -gt 0 ]; do
  case "$1" in
    --docs) DOCS="$2"; shift 2 ;;
    --doc-size) DOC_SIZE="$2"; shift 2 ;;
    --duration) DURATION="$2"; shift 2 ;;
    --backends) BACKENDS="$2"; shift 2 ;;
    --limiters) LIMITERS="$2"; shift 2 ;;
    --driver-concurrency) DRIVER_CONCURRENCY="$2"; shift 2 ;;
    --query) QUERY="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --interval) INTERVAL="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# Load nvm-provided node if the system has none.
if ! command -v node >/dev/null 2>&1; then
  export NVM_DIR="${NVM_DIR:-$HOME/.nvm}"
  # shellcheck disable=SC1091
  [ -s "$NVM_DIR/nvm.sh" ] && . "$NVM_DIR/nvm.sh" >/dev/null 2>&1
fi
if ! command -v node >/dev/null 2>&1; then
  echo "!! node not found (needed for the load driver)"; exit 1
fi

if [ ! -x "$BIN" ]; then
  echo ">> building release cosmos-cli"
  ( cd "$RUST_DIR" && cargo build --release -p cosmos-cli ) || exit 1
fi

mkdir -p "$WORKDIR"
RESULTS="$WORKDIR/results.csv"
echo "backend,limiter,driver_concurrency,docs,doc_size,duration_s,qps,p50_ms,p95_ms,p99_ms,peak_rss_mib,peak_vmhwm_mib,peak_cpu_pct,mean_cpu_pct" > "$RESULTS"

SERVER_PID=""
cleanup() {
  [ -n "$SERVER_PID" ] && kill "$SERVER_PID" >/dev/null 2>&1
  "$BIN" stop >/dev/null 2>&1 || true
}
trap cleanup EXIT

boot() { # backend limiter data_dir
  local backend="$1" limiter="$2" data_dir="$3"
  "$BIN" stop >/dev/null 2>&1 || true
  rm -rf "$data_dir"; mkdir -p "$data_dir"
  "$BIN" start --key "$KEY" --port "$PORT" --mongo-port "$MONGO_PORT" \
    --storage "$backend" --max-concurrent-queries "$limiter" \
    --disable-throughput-enforcement \
    --data-dir "$data_dir" > "$WORKDIR/server-$backend-$limiter.log" 2>&1 &
  SERVER_PID=$!
  for _ in $(seq 1 40); do
    if curl -s -o /dev/null "http://localhost:$PORT/" \
        -H "x-ms-cosmos-explorer: 1" -H "x-ms-version: 2018-12-31" 2>/dev/null; then
      return 0
    fi
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      echo "!! server exited early"; cat "$WORKDIR/server-$backend-$limiter.log"; return 1
    fi
    sleep 0.5
  done
  echo "!! server not ready"; cat "$WORKDIR/server-$backend-$limiter.log"; return 1
}

for backend in $BACKENDS; do
  for L in $LIMITERS; do
    echo
    echo "================================================================"
    echo ">> backend=$backend  limiter=$L  driver-concurrency=$DRIVER_CONCURRENCY"
    echo "================================================================"
    data_dir="$WORKDIR/data-$backend-$L"
    boot "$backend" "$L" "$data_dir" || { SERVER_PID=""; continue; }

    # Seed the container (fresh per boot; in-memory loses data on restart).
    node "$DRIVER" seed --endpoint "http://localhost:$PORT" --key "$KEY" \
      --docs "$DOCS" --doc-size "$DOC_SIZE" --concurrency 32 || { cleanup; SERVER_PID=""; continue; }

    # Start the resource sampler on the server PID.
    samp_csv="$WORKDIR/rss-$backend-$L.csv"
    bash "$SAMPLER" --pid "$SERVER_PID" --interval "$INTERVAL" \
      --csv "$samp_csv" --label "$backend-L$L" > "$WORKDIR/sample-$backend-$L.out" 2>&1 &
    SAMP_PID=$!

    # Run the load.
    load_out="$WORKDIR/load-$backend-$L.out"
    node "$DRIVER" load --endpoint "http://localhost:$PORT" --key "$KEY" \
      --concurrency "$DRIVER_CONCURRENCY" --duration "$DURATION" \
      --query "$QUERY" | tee "$load_out"

    # Stop the sampler and collect its summary.
    kill "$SAMP_PID" >/dev/null 2>&1
    wait "$SAMP_PID" 2>/dev/null
    samp_line="$(grep '^SAMPLE_RESULT' "$WORKDIR/sample-$backend-$L.out" | tail -1)"
    load_json="$(grep '^PERF_LOAD_JSON' "$load_out" | tail -1 | sed 's/^PERF_LOAD_JSON //')"

    # Extract metrics with node (robust JSON parse) + shell for the sampler line.
    read -r qps p50 p95 p99 <<EOF2
$(node -e 'const s=JSON.parse(process.argv[1]); console.log(s.throughput_qps, s.latency_ms.p50, s.latency_ms.p95, s.latency_ms.p99)' "$load_json" 2>/dev/null)
EOF2
    peak_rss="$(echo "$samp_line" | sed -n 's/.*peak_rss_mib=\([0-9.]*\).*/\1/p')"
    peak_hwm="$(echo "$samp_line" | sed -n 's/.*peak_vmhwm_mib=\([0-9.]*\).*/\1/p')"
    peak_cpu="$(echo "$samp_line" | sed -n 's/.*peak_cpu_pct=\([0-9.]*\).*/\1/p')"
    mean_cpu="$(echo "$samp_line" | sed -n 's/.*mean_cpu_pct=\([0-9.]*\).*/\1/p')"

    echo "$backend,$L,$DRIVER_CONCURRENCY,$DOCS,$DOC_SIZE,$DURATION,${qps:-NA},${p50:-NA},${p95:-NA},${p99:-NA},${peak_rss:-NA},${peak_hwm:-NA},${peak_cpu:-NA},${mean_cpu:-NA}" >> "$RESULTS"

    cleanup; SERVER_PID=""; sleep 1
  done
done

echo
echo "================ perf results ================"
column -s, -t < "$RESULTS"
echo "============================================="
echo "PERF_RESULTS_CSV $RESULTS"
