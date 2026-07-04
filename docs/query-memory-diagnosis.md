# Query Memory Saturation — Diagnosis, Root Cause & Resolution

**Status:** Resolved
**Area:** `src/NoSql/Query` · `src/NoSql/Controllers/DocumentsController.cs`
**Symptom:** Emulator process RAM climbs to tens of GB (~23 GB observed) under query load and does **not** fall when the load stops, giving the appearance of an unbounded memory leak.

---

## 1. Summary

Under concurrent query load against a large collection, the emulator's working set grew to ~23 GB and stayed there. Investigation showed this was **not a rooted (permanent) leak** but **allocation-rate saturation**: every query fully materializes the entire target container into `JsonNode` object graphs before paging. At high concurrency the garbage collector cannot reclaim these short-lived graphs fast enough, so the heap balloons and the GC effectively collapses.

The fix bounds query concurrency with a `SemaphoreSlim` gate (`QueryExecutionLimiter`) applied only at the top-level query entry point, configurable via `EmulatorOptions.MaxConcurrentQueries`. After the fix, a concurrency-64 soak over 21,000 documents stayed **flat at ~608 MB (peak 822 MB)** and settled back to ~460 MB once load stopped.

---

## 2. Reproduction

- **Data:** one collection seeded with **21,000 documents** on the default **Sqlite** backend (file-backed `emulator.db`).
- **Load:** batches of `SELECT`-style queries issued at **concurrency 64** via a small .NET load driver.
- **Auth:** requests used the `x-ms-cosmos-explorer` header to bypass HMAC (see `CosmosAuthMiddleware.IsExplorerRequest`), so all requests reached the controller.
- **Observation:** RAM climbed 7 GB → 10 GB → **23 GB** and did **not** drop after the load ended.

> **Seeding gotcha:** containers default to 400 RU, so writes/queries are `429`-throttled (throttled requests never reach the controller and look like "hangs" or "lock contention"). Set a high `maxThroughput` in the container-create body when seeding or load-testing.

---

## 3. Diagnosis procedure

The steps below are the general procedure for distinguishing a **rooted leak** (objects kept alive forever) from **allocation-rate saturation** (transient objects the GC can't keep up with). This is reusable for future memory investigations on the emulator.

1. **Confirm the write path is innocent.**
   Seed the collection while sampling process RAM. During the 21K-document seed, RAM stayed flat (~60–160 MB). ⇒ The write/persistence path (`SqliteDocumentStore`, LSN write loop, change feed) does **not** leak.

2. **Reproduce under query load and watch the trend, not just the peak.**
   Run the query soak at high concurrency and sample RSS on an interval. RAM climbed to 23 GB and stayed there after load stopped.

3. **Rule out heap-profiler tooling on a saturated heap.**
   `dotnet-gcdump` and `dotnet-counters` both hang or are impractically slow against a ~23 GB heap under active load — **do not** wait on them here. Use a controlled low-concurrency experiment instead (next step).

4. **Run the decisive control experiment: same data, low concurrency.**
   Restart the emulator (data persists in `emulator.db`) and re-run the *identical* query workload at **concurrency 4**.
   - Result: **flat, peak 471 MB.**
   - Interpretation: identical queries over identical data are cheap in aggregate; only *simultaneity* blows up memory. A rooted leak would still grow at low concurrency — it didn't. ⇒ **Allocation-rate saturation, not a leak.**

5. **Localize the allocation source by reading the hot path.**
   Trace the top-level query entry (`DocumentsController` → `CosmosQueryEngine.ExecuteQueryAsync`). The engine loads and parses **every** document in the container per query before paging.

6. **Estimate peak transient allocation.**
   `peak ≈ concurrency × containerSize × JsonNode-overhead`. At concurrency 64 over 21K documents this is enough simultaneous live allocation to collapse the GC — matching the 23 GB observation and the flat 471 MB result at concurrency 4.

---

## 4. Root cause

`CosmosQueryEngine.ExecuteQueryAsync` performs a **full container materialization on every query, before paging**:

- `ListDocumentsAsync(...)` reads and parses **all** documents in the container into `JsonNode` graphs.
- `.Where(IsIndexed).ToList()` buffers the entire parsed set into a `List`.
- Each surviving document is converted with a per-document `ToResponseBody()`.
- Only *after* all of the above is the result set paged/limited.

Because indexing metadata is stored but **not** used for query optimization (all queries scan all documents — see *Known intentional limitations* in `docs/architecture.md`), there is no early termination: a `SELECT TOP 10` still materializes the whole collection.

At high concurrency, N queries each build a full-container `JsonNode` graph **simultaneously**. Peak live allocation scales as `concurrency × containerSize`, exceeding what the GC can reclaim in time. The heap grows to accommodate the allocation rate and does not shrink promptly afterward — which *looks like* a leak but is really the GC operating at a permanently enlarged working set.

**Why it is not a rooted leak:** no query result is retained past the request. The control experiment (step 4) proved memory stays flat at low concurrency with the same data, which excludes a permanent-retention bug.

---

## 5. Resolution

Bound the number of **concurrent top-level query executions** so peak simultaneous materialization stays within GC reach.

### Change: `QueryExecutionLimiter`

- New `IQueryExecutionLimiter` / `QueryExecutionLimiter` (`src/NoSql/Query/QueryExecutionLimiter.cs`): a `SemaphoreSlim` gate exposing `AcquireAsync(ct)` that returns a disposable release token.
- `DocumentsController` acquires the gate **only around the top-level** `ExecuteQueryAsync`:

  ```csharp
  using (await _queryLimiter.AcquireAsync(ct))
  {
      // ExecuteQueryAsync(...)
  }
  ```

- **Applied at the controller boundary only.** Subqueries call the query engine directly (not through the controller), so they do not re-enter the gate — this avoids a recursion self-deadlock where an outer query holding the semaphore waits on an inner query that can never acquire it.

### Configuration

- `EmulatorOptions.MaxConcurrentQueries` — default `Math.Max(2, Environment.ProcessorCount / 2)` (e.g. 8 on a 16-core host).
- Raising it trades memory for query parallelism; lowering it caps memory further.

### DI registration (three surfaces — keep in sync)

`IQueryExecutionLimiter` must be registered in all three service-registration paths, or query requests fail with `Unable to resolve service`:

1. `src/Host/Program.cs` — production startup
2. `src/Host/HostApplication.cs` — CLI / test startup
3. `tests/NoSql.Tests/TestServerFixture.cs` — test fixture

---

## 6. Validation

Re-ran the concurrency-64 soak over 21,000 documents after the fix:

| Metric | Before | After |
| --- | --- | --- |
| Trend under sustained load | 7 → 10 → **23 GB**, no recovery | **Flat**: first-third avg 608 MB ≈ last-third avg 608 MB |
| Peak RSS | ~23 GB | **822 MB** |
| Settled RSS after load stops | stayed ~23 GB | ~460 MB |
| Control @ concurrency 4 | (n/a) | peak 471 MB |
| 2,000-query batch | — | 0 failures, all HTTP 200 |

Test suite: query + telemetry tests **58/58 pass**; overall 256/264 (the 8 failures are pre-existing `BatchTests` unrelated to this change).

**Trade-off:** the gate caps sustained query throughput (~5 q/s under a concurrency-64 burst at the default gate of 8). This is configurable via `MaxConcurrentQueries` for workloads that prefer throughput over a lower memory ceiling.

---

## 7. Related follow-ups (not required for the fix)

The underlying inefficiency — full-container materialization per query — remains an intentional limitation of the hand-rolled query engine. Future optimization options, if query throughput becomes a priority:

- Stream/enumerate documents lazily instead of `ToList()`-buffering the whole container.
- Apply `TOP`/`OFFSET`/`LIMIT` and simple predicate filters during the scan to bound per-query allocation before paging.
- Use stored index metadata to skip non-matching documents.

Until then, `MaxConcurrentQueries` is the supported lever for keeping memory bounded.
