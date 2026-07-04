# DiskANN / HNSW Vector Query Acceleration

How the emulator turns `ORDER BY VectorDistance(...)` into a real
index-accelerated nearest-neighbour search — the diagnosis of why it *wasn't*
accelerating for real clients, the fixes, and the measured results.

> Companion to [`vector-search.md`](vector-search.md), which documents the feature
> and its configuration. This document focuses on the **query-acceleration bug**:
> its root cause, resolution, and validation.

---

## 1. Symptom

A client application issuing vector queries against a large container reported:

> "A ~16 s floor for tiny partitions plus linear growth for the big one means the
> query is still doing a full brute-force scan — the DiskANN index is defined but
> not used at query time (either still building indefinitely, or this emulator
> preview doesn't execute DiskANN search for 3072-dim vectors). 20k×3072 vectors
> is heavy."

Two independent tells:

- **A ~16 s floor even for tiny partitions** → a fixed per-query cost unrelated to
  result size (the whole container being materialized into `JsonNode` graphs on
  every query).
- **Linear growth with the big partition** → the query cost scales with the
  container/partition size → a full brute-force scan, not an index lookup.

The container that reproduced it: **`podcast-assistant/vector-stores-embeddings`**,
**20,735 documents × 3072 dims**, cosine, index type `diskANN`, in the local
emulator's SQLite store (`%LOCALAPPDATA%\CosmosEmulator\data\emulator.db`).

---

## 2. Diagnosis procedure

1. **Locate the fast-path trigger.**
   `CosmosQueryEngine.TryBuildVectorSearchRowsAsync` is the code that decides
   whether a query can be served from the vector index. It has a series of
   bail-out conditions; if any is hit it returns `null` and the engine falls back
   to the full-scan path (`ApplyOrdering`).

2. **Read the ordering condition.** The trigger accepted the query only when the
   requested `ORDER BY` direction matched the index's "nearest-first" direction,
   which it computed **per distance function**:

   ```csharp
   // OLD (buggy)
   var nearestFirstRequested = distanceFunction == VectorDistanceFunction.Euclidean
       ? !clause.Descending   // euclidean: ascending == nearest
       : clause.Descending;   // cosine / dotproduct: DESCENDING == nearest
   if (!nearestFirstRequested) return null;   // ← bail to full scan
   ```

   For cosine/dot-product this required **`DESC`** to accelerate.

3. **Compare against the real client query and Azure semantics.** The idiomatic
   Azure Cosmos DB nearest-neighbour query uses **no `ORDER BY` direction**
   (ascending) and returns the nearest matches first:

   ```sql
   SELECT TOP 10 c.id, VectorDistance(c.embedding, @q) AS score
   FROM c
   ORDER BY VectorDistance(c.embedding, @q)      -- ascending / no direction
   ```

   Verified against the official docs
   (`learn.microsoft.com/azure/cosmos-db/nosql/query/vectordistance`): the ORDER BY
   is by **proximity** (nearest first) regardless of distance function; the 3rd
   argument is a **boolean brute-force flag**; the 4th argument is an options
   object.

4. **Conclude the mismatch.** With the client's ascending query,
   `clause.Descending == false`, so for cosine `nearestFirstRequested == false` →
   the fast path **bailed on every query**. Worse, the index build
   (`EnsureIndexAsync`) is invoked *after* that bail, so **the index was never
   built at all** — matching "defined but not used / building indefinitely."

5. **Read the fallback.** `ApplyOrdering` sorted by the raw `VectorDistance` scalar.
   For cosine that scalar is a *similarity* (higher = closer), so an ascending sort
   put the **farthest** documents first — the fallback was also mis-ordering.

6. **Read the provider.** `HnswVectorIndexProvider.SearchAsync` contained:

   ```csharp
   var useExact = shard.Graph is null || request.PartitionKey is not null;
   ```

   i.e. **any** query carrying the `x-ms-documentdb-partitionkey` header
   (every single-partition query) was forced onto the exact brute-force branch,
   never the graph — a second, independent reason large single-partition queries
   were slow.

7. **Reproduce & measure.** A throwaway in-process harness opened a **copy** of the
   live `emulator.db`, wired the real storage + vector + query-engine stack, and
   timed the idiomatic ascending query cross-partition and single-partition.

---

## 3. Root causes

| # | Root cause | Effect |
| --- | --- | --- |
| 1 (primary) | **ORDER BY direction convention diverged from Azure.** Fast path required `DESC` for cosine/dot-product; the real client used ascending. | Fast path bailed on every idiomatic query → full brute-force scan; index never built. |
| 2 | **Fallback sorted by the raw similarity scalar.** | Ascending cosine sort returned farthest-first (wrong order). |
| 3 (secondary) | **Partition-scoped queries always brute-forced** (`request.PartitionKey is not null ⇒ exact scan`). | Single-partition vector queries never used the graph. |
| 4 | **Boolean brute-force argument ignored.** | No way to opt into/out of the index per Azure semantics. |
| 5 | **First-query build stalled synchronously** (~66–74 s for 20k×3072). | Reinforced the "still building / brute force" perception. |

The **~16 s floor** = materializing all 20,735 documents into `JsonNode` graphs per
query in the fallback path. The **linear growth** = that cost scaling with
container/partition size.

---

## 4. Resolution

All changes preserve the **scalar values** returned by `VectorDistance` (cosine
identical = `1.0`, euclidean = distance, dot-product = dot). Only the **ordering
semantics** and the **index execution paths** changed.

### 4.1 Azure nearest-first ordering (`order-semantics`)

`ORDER BY VectorDistance(...)` now orders by **proximity** for every function:
ascending / no direction = nearest first, `DESC` = farthest first.

- **Fast-path trigger** (`CosmosQueryEngine.TryBuildVectorSearchRowsAsync`):

  ```csharp
  // NEW
  var nearestFirstRequested = !clause.Descending;   // ascending == nearest, all functions
  if (!nearestFirstRequested) return null;          // DESC (farthest-first) → fallback
  ```

- **Fallback** (`CosmosQueryEngine.ApplyOrdering`): when the first ORDER BY key is
  `VectorDistance`, sort by a **nearest-first key** (lower = closer): negate the
  similarity for cosine/dot-product, use the distance directly for euclidean;
  `clause.Descending` reverses it. Undefined/missing embeddings remain excluded.

Only the **nearest-first (ascending)** case is index-accelerated; a `DESC`
(farthest-first) request uses the correctly-ordered fallback (rare in practice).

### 4.2 Partition-scoped ANN (`partition-ann`)

`HnswVectorIndexProvider.Shard` now maintains a
`Dictionary<string, List<int>> PartitionEntries` (partition-key header → entry ids),
kept current on build / append / delete / rebuild. `SearchAsync` for a
single-partition query:

- **Small partition** (≤ `PartitionExactScanThreshold` live vectors, default 4096)
  or graph not yet built → **exact-scan just that partition's entries** (fast,
  exact, `O(partition)`).
- **Large partition** → **graph KNN with partition-filtered adaptive over-fetch**
  (`PartitionFilteredGraphSearch`): widen the candidate set (`k *= 4`) until at
  least `TopK` live entries from the target partition are found or the whole graph
  is scanned.

### 4.3 Boolean brute-force flag (`brute-force-arg`)

`VectorDistance(v1, v2, <bool>[, <options>])` — a `true` third argument now forces
an exhaustive scan and bypasses the fast path, matching Azure. `false`/omitted uses
the index. (Inline JSON object literals for the 4th options arg remain unsupported
by the parser; pass options via a `@parameter`.)

### 4.4 Non-blocking background build (`background-build`)

`VectorIndexOptions.BackgroundBuild` (default **true**): the expensive HNSW graph
build runs on a background thread (`BuildGraphInBackground`). The shard's entries
are populated synchronously, so the first queries return immediately via an exact
brute-force scan and transparently switch to graph acceleration once the build
publishes — no multi-second first-query stall.

Concurrency safety:
- The expensive `graph.AddItems(snapshot)` runs **outside** the shard lock; the
  finished graph is published **under** the write lock.
- Entries appended while building are reconciled into the graph at publish time
  (their ids stay aligned with `shard.Entries` indices).
- A `Shard.BuildGeneration` counter is bumped on every full `Rebuild`; a background
  build whose generation no longer matches is **discarded** to avoid publishing a
  stale graph.

### 4.5 New configuration

Bound from `"Emulator:VectorIndex"` into `VectorIndexOptions`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `PartitionExactScanThreshold` | `4096` | Max live vectors in a partition for which a partition-scoped query exact-scans that partition instead of using the graph. |
| `BackgroundBuild` | `true` | Build the HNSW graph off the query thread; brute-force until ready, then accelerate. Set `false` for deterministic build-before-first-query behavior (e.g. tests). |

---

## 5. Files changed

| File | Change |
| --- | --- |
| `src/NoSql/Query/CosmosQueryEngine.cs` | Nearest-first fast-path trigger; boolean brute-force arg; nearest-first fallback ordering; `ResolveOrderingVectorFunction` helper. |
| `src/Storage/Vector/HnswVectorIndexProvider.cs` | Partition-scoped ANN (`PartitionEntries`, `PartitionFilteredGraphSearch`); background build (`BuildGraphInBackground`, `BuildGeneration`); partition-map maintenance in build/append/rebuild. |
| `src/Core/Models/VectorIndexOptions.cs` | `PartitionExactScanThreshold`, `BackgroundBuild`. |
| `tests/NoSql.Tests/VectorSearchTests.cs` | `VectorDistance_OrderBy_TopN` switched to ascending convention. |
| `tests/NoSql.Tests/VectorIndexAcceleratedTests.cs` | Cosine tests switched to ascending; new tests for DESC=farthest, boolean brute-force arg, and partition-scoped graph search; `CreateSut` accepts `VectorIndexOptions`. |
| `docs/vector-search.md`, `docs/api-compatibility.md` | Ordering convention, partition ANN, boolean arg, background build. |

---

## 6. Validation

### 6.1 Tests

- `tests/NoSql.Tests` — **273 passed** (23 vector tests, incl. new partition /
  boolean / DESC-farthest cases).
- `tests/Core.Tests` — **19 passed**.
- Full solution builds clean (`dotnet build Azure.Cosmos.LightEmulator.slnx`),
  nullable + warnings-as-errors.

New behavioral tests of note:
- `OrderByVectorDistance_Descending_ReturnsFarthestFirst`
- `VectorDistance_BooleanBruteForceArg_ReturnsNearestFirst`
- `PartitionScopedQuery_UsesGraph_ReturnsOnlyThatPartitionNearest`
  (threshold set below the per-partition count to force the graph path)

### 6.2 Live container benchmark

Against a **copy** of the real `emulator.db`
(`podcast-assistant/vector-stores-embeddings`, 20,735 × 3072, cosine, 3 partitions,
largest = 20,602 docs), running the idiomatic **ascending** query
`SELECT TOP 10 c.id FROM c ORDER BY VectorDistance(c.embedding, @q)`:

| Query | Latency |
| --- | --- |
| Cold cross-partition (incl. one-time index build) | ~74 s |
| **Warm cross-partition** | **p50 ≈ 10 ms**, p95 ≈ 24 ms |
| **Warm single-partition** (the 20,602-doc partition) | **p50 ≈ 9 ms**, p95 ≈ 23 ms |

Before the fix the same ascending query hit the full-scan fallback (~16 s floor,
growing with size). After the fix it is **~1000× faster warm** and both the
cross-partition and large single-partition paths are index-accelerated.

> The benchmark harness was a throwaway console app under `scratch/` that copied
> the live DB (never mutating the original) and exercised the real
> storage → vector → query-engine stack in-process. It was **removed** after
> validation; reproduce by wiring `SqliteDocumentStore` +
> `VectorIndexingDocumentStore` + `HnswVectorIndexProvider` +
> `CosmosQueryEngine` against a copy of the data directory.

---

## 7. Behavioral change (breaking)

The emulator's `ORDER BY VectorDistance` **direction now matches Azure**:
ascending / no direction = nearest first. Earlier builds required `DESC` for
cosine/dot-product to get nearest-first; that non-standard convention was removed.
Any emulator-specific queries or tests that relied on `DESC`-means-nearest must
switch to ascending. The **scalar values** returned by `VectorDistance` are
unchanged.
