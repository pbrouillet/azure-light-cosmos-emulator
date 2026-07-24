# Perf & load validation — RAM & CPU

This document reports memory and CPU behavior of the Rust/Axum Cosmos emulator
under concurrent query load, and how to reproduce the runs. It focuses on the
**known memory hotspot**: `SqlQueryEngine::execute_query` materializes the
**entire container** into `serde_json::Value` graphs on every query, so peak RSS
scales with the number of queries executing concurrently — bounded only by the
`QueryExecutionLimiter` (`--max-concurrent-queries`, default `max(2, CPU/2)`).

> The numbers below are **indicative**, measured on a shared developer host, not
> an SLA. They illustrate *scaling and bounding behavior*, which is portable;
> absolute qps/latency will differ per machine.

## Tooling

All under `rust/perf/` (no external load tool, no npm install — Node built-ins
only):

| Script | Role |
|---|---|
| `query_load.js` | `seed` a container with N docs of ~S bytes, then `load` it with C concurrent full-scan SQL queries for D seconds. Emits throughput + p50/p95/p99 latency as JSON. Auth via the local `x-ms-cosmos-explorer` bypass header. |
| `sample_resources.sh` | Samples `/proc/<pid>/status` (VmRSS/VmHWM) and `/proc/<pid>/stat` (utime+stime → CPU%) at a fixed interval; reports peak RSS, peak `VmHWM`, and peak/mean CPU%. |
| `run_perf.sh` | Orchestrator: builds the release binary, and for each `{backend} × {limiter}` combo boots the emulator, seeds the container, runs the load while sampling resources, and aggregates a CSV/table. |

Two emulator knobs were added to make this measurable (both also improve .NET
parity / usability):

- **`--max-concurrent-queries <n>`** — wires `HostOptions.max_concurrent_queries`
  into the query engine's `QueryExecutionLimiter` (was hardcoded to
  `new_default()`; .NET exposes the equivalent `MaxConcurrentQueries`). `None`
  keeps the `max(2, CPU/2)` default.
- **`--disable-throughput-enforcement`** — turns off RU/429 enforcement so a load
  run measures raw memory/CPU instead of being throttled.

## Method

- Workload: a full-container-scan query `SELECT c.id, c.value FROM c WHERE
  c.value > 500` (no index acceleration — every query materializes the whole
  container, which is the path under test).
- Container: **20,000 documents × ~1 KiB** (≈ 20 MB of raw JSON) across 16
  logical partitions.
- **Driver concurrency fixed at 64** so the server-side limiter `L` is the
  binding constraint. Sweep `L ∈ {1, 4, 16, 64}`.
- Backends: **sqlite** (default, file-backed) and **in-memory**.
- 15 s of load per combo; resources sampled every 200 ms (5 Hz); peak RSS is the
  max VmRSS observed during the run.

Reproduce:

```bash
rust/perf/run_perf.sh --docs 20000 --doc-size 1024 --duration 15 \
    --backends "sqlite in-memory" --limiters "1 4 16 64" --driver-concurrency 64
```

## Environment

| | |
|---|---|
| CPU | 12th Gen Intel Core i7-1270P — **16 logical cores** |
| RAM | 45 GiB |
| OS | Ubuntu 26.04 LTS (Linux 7.0) |
| Node | v26 (nvm) |
| Build | `cargo build --release -p cosmos-cli` |

## Results

Peak RSS (MiB), CPU% (100% = one core), throughput (queries/s), and query
latency, by backend × query-concurrency limiter. Driver concurrency = 64.

| Backend | Limiter L | Peak RSS (MiB) | Peak CPU% | Mean CPU% | Throughput (q/s) | p50 (ms) | p95 (ms) | p99 (ms) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| sqlite    | 1  | 777  | 105 | 99  | 2.8 | 19176 | 23099 | 23158 |
| sqlite    | 4  | 2017 | 254 | 186 | 3.7 | 16142 | 17562 | 17772 |
| sqlite    | 16 | 4001 | 217 | 188 | 3.7 | 13277 | 23775 | 27168 |
| sqlite    | 64 | 4001 | 230 | 187 | 3.6 | 13978 | 22060 | 24020 |
| in-memory | 1  | 912  | 106 | 99  | 4.4 | 14082 | 14411 | 14428 |
| in-memory | 4  | 1861 | 412 | 369 | 8.2 | 7509  | 7744  | 8111  |
| in-memory | 16 | 3997 | 526 | 409 | 7.6 | 7031  | 10741 | 16453 |
| in-memory | 64 | 3997 | 608 | 409 | 6.7 | 8022  | 13351 | 15057 |

(Raw CSV: `run_perf.sh` prints `PERF_RESULTS_CSV <path>`; per-sample RSS/CPU
traces are written alongside as `rss-<backend>-<L>.csv`.)

## Analysis

**1. The limiter bounds peak RAM — this is the headline.**
Peak RSS rises monotonically with `L`: from **~0.8–0.9 GiB at L=1** to **~4 GiB**
when unbounded. Each concurrently-executing query materializes the whole
container into a `serde_json::Value` graph before paging, so
`peak_RSS ≈ baseline + (in-flight queries) × (materialized container size)`.
For this ~20 MB container, one in-flight full scan costs **~0.6–0.8 GiB** of
transient RSS — a ~30–40× blow-up over the raw JSON, driven by `Value`/`HashMap`
node overhead. Lowering `L` is a direct, effective RAM cap.

**2. Effective concurrency saturates at the core count.**
RSS and throughput **plateau between L=16 and L=64** (identical ~4 GiB peak).
These full-scan queries are CPU-bound, so real in-flight concurrency is
`min(L, physical parallelism)`; on this 16-core box, permits beyond ~16 don't add
simultaneous work (or memory). The limiter is a ceiling, not a target.

**3. Extrapolation to the documented ~23 GiB incident.**
At ~0.7 GiB per in-flight scan of a 20 MB container, an **unbounded** engine on a
larger container (or more cores) reaches tens of GiB: e.g. a ~150 MB container ×
16 in-flight ≈ ~23 GiB — matching the historical "memory leak". The default
`max(2, CPU/2)` (here 8) keeps peak ~2–3 GiB; setting `--max-concurrent-queries 1`
pins it near ~0.8 GiB.

**4. CPU scales with `L` up to the core count.**
CPU climbs from ~1 core (L=1, ~100%) toward multiple cores (in-memory L=64 ≈
608% ≈ 6 cores). **in-memory** is pure-CPU and both faster (≈2× throughput) and
hotter than **sqlite**, which spends time in SQLite reads (lower CPU%, lower qps).

**5. Throughput/latency under heavy concurrent scans is intentionally poor.**
Whole-container materialization per query means multi-second latencies and single-
digit qps at 64-way concurrency. Raising `L` past the core count does not help;
it only raises peak RAM headroom. This is the tradeoff the limiter manages.

## Recommendations

- **Keep the `max(2, CPU/2)` default.** It bounds peak RAM to a few GiB on
  typical dev boxes while allowing useful parallelism.
- **Tune `--max-concurrent-queries` per host:** set `1–2` on memory-constrained
  machines/containers; raise it on big boxes only if you budget
  `~(container size × 30–40) × L` of transient RSS.
- **The real fix is streaming/paged query execution** — evaluating and paging
  without loading the entire container into `Value` graphs per call. That would
  flatten the RSS-vs-concurrency curve and is the recommended future refinement
  (noted in the query-engine roadmap).
- Use `--disable-throughput-enforcement` **only** for load/perf runs, never as a
  general default (it removes RU/429 semantics).

## See also

- `rust/perf/` — the scripts above.
- `rust/PARITY.md` — feature-parity map and the always-on test harness.
- Query-engine memory notes in the repository `docs/` and the engine source
  (`rust/crates/query/src/engine.rs`, `services.rs`).
