# Vector Search & Indexing

The emulator implements **index-accelerated vector search** for the Cosmos DB
`VectorDistance` function. Instead of scanning and materializing an entire
container per query (the original behavior — see below), the query engine uses an
in-memory **HNSW approximate-nearest-neighbour (ANN)** index and materializes
only the top‑K matching documents.

> For the diagnosis of why acceleration wasn't triggering for real Azure clients
> (ordering convention, partition-scoped queries, background build) and the
> measured before/after, see [`DISKANN.md`](DISKANN.md).

## TL;DR

- `ORDER BY VectorDistance(c.embedding, <query>)` is served from an ANN index.
- Works out-of-the-box for containers **without** a declared vector policy
  (implicit indexing) and honors a declared `vectorEmbeddingPolicy` /
  `VectorIndexes` when present.
- Scales to **100K+ vectors** with **sub-second** query latency and **bounded**
  memory.
- Uses the **Azure Cosmos DB ordering convention**: `ORDER BY VectorDistance(...)`
  **ascending / no direction returns nearest first** (for every distance
  function); `DESC` returns farthest first — no query rewrites required for real
  Azure clients.

## Why

The previous implementation loaded **every** document in the container into
`JsonNode` graphs, computed the distance per document, then sorted `O(N log N)`.
Measured cost on a real container of **20,735 docs × 3072 dims**:

| Metric | Before (full scan) | After (HNSW index) |
| --- | --- | --- |
| Query latency (p50 / p95) | ~74,000 ms | **10 ms / 14 ms** |
| Transient memory per query | ~10–15 GB (whole container materialized) | top‑K only |
| Resident index memory | n/a | **~251 MB** (= 20,735 × 3072 × 4 B) |

Under concurrent load the full-container materialization saturated the GC — this
was the root cause of the "memory leak" investigation.

## Measured scaling

Synthetic set of **100,000 docs × 384 dims** (cosine), single node:

| Metric | Value |
| --- | --- |
| Query latency (p50 / p95) | **3.8 ms / 4.6 ms** |
| Resident index memory | **~212 MB** |
| One-time index build | ~8 min (lazy, cached; see *Build cost* below) |

Query latency stays in the **low-milliseconds** range regardless of container
size because HNSW visits `~efSearch × M` nodes rather than all `N`. Resident
memory grows **linearly**: `≈ dims × 4 bytes × count` plus a small graph
overhead. For example, 100K × 3072 float32 ≈ **1.2 GB** resident.

## How it works

```
DocumentsController
   └─ CosmosQueryEngine.ExecuteQueryAsync
        ├─ TryBuildVectorSearchRowsAsync  ── fast path (ANN)
        │     └─ IVectorIndexProvider (HnswVectorIndexProvider)
        └─ full-scan fallback             ── unsupported shapes
```

- **`HnswVectorIndexProvider`** (`src/Storage/Vector/`) holds one **shard** per
  `(database, container, embeddingPath)`. Each shard is a
  `SmallWorld<float[], float>` HNSW graph (or a flat list for exact search) plus
  the entry vectors, a `docKey → id` map, and a tombstone count, guarded by a
  `ReaderWriterLockSlim`. Shards are built **lazily** from the backing store on
  first query for a path and cached.
- **`VectorIndexingDocumentStore`** is an `IDocumentStore` **decorator** that
  keeps shards current on create / replace / upsert / patch / delete / empty /
  container drop, delegating all storage to the inner store (Sqlite / InMemory /
  SurrealDb). HNSW has no native delete, so updates/deletes are **tombstoned**
  and the graph is rebuilt once the tombstone ratio exceeds
  `RebuildTombstoneRatio`.
- The query engine recognizes the vector-search shape (single
  `ORDER BY VectorDistance(path, vector)`, bounded `TOP k` / `OFFSET…LIMIT`,
  optional `WHERE`, optional `VectorDistance(...) AS score`), resolves the query
  vector and distance function, asks the provider for nearest‑first candidates,
  materializes only those, then runs the normal projection / paging pipeline.
  Any unsupported shape falls back to the full-scan path (which still applies the
  undefined-embedding-exclusion parity fix).

## Distance functions & ordering

`ORDER BY VectorDistance(...)` orders by **proximity**, exactly like Azure Cosmos
DB: **ascending / no direction == nearest first**, `DESC` == farthest first —
independent of the distance function. Only the *scalar value* returned by
`VectorDistance` differs per function:

| Function | `VectorDistance` scalar value | `ORDER BY` nearest-first |
| --- | --- | --- |
| `cosine` | similarity (higher = closer, 1.0 = identical) | ascending / no direction |
| `dotproduct` | dot product (higher = closer) | ascending / no direction |
| `euclidean` | distance (lower = closer) | ascending / no direction |

> **Azure parity note.** The idiomatic Azure query is
> `SELECT TOP k c.id, VectorDistance(c.v, @q) AS score FROM c ORDER BY VectorDistance(c.v, @q)`
> (no `ASC`/`DESC`) and returns the nearest neighbours first. Earlier emulator
> builds required `DESC` for cosine/dot-product; that non-standard convention was
> **removed** — ascending is now nearest-first for all functions, matching Azure.

- The index-accelerated fast path triggers only for the **nearest-first**
  (ascending / no-direction) case; a `DESC` (farthest-first) request uses the
  fallback path (still correctly ordered).
- Documents whose embedding is missing, `undefined`, or of the wrong
  dimensionality are **excluded** from vector-ordered results (matching Cosmos +
  a vector index), on both paths.
- The scalar values returned by `VectorDistance(...)` (e.g. cosine `1.0` for
  identical vectors) are **unchanged**.

### Brute-force flag (3rd argument)

`VectorDistance(v1, v2, <bool>[, <options>])` accepts a boolean third argument. As
in Azure, passing `true` forces an **exhaustive (brute-force) scan** and bypasses
the ANN index; `false` or omitting it uses the index. Results are identical in
order; only the execution strategy differs. (The 4th `options` object — e.g.
`{"distanceFunction": "cosine"}` — is honored when supplied via a parameter;
inline JSON object literals are not supported by the query parser.)

### Partition-scoped queries

A query that carries the `x-ms-documentdb-partitionkey` header (a single-partition
query) is served **within that partition**:

- **Small partitions** (≤ `PartitionExactScanThreshold` live vectors) are
  exact-scanned over just that partition's entries — fast and exact.
- **Large partitions** use the HNSW graph with a **partition-filtered adaptive
  over-fetch**: the candidate set is widened until enough neighbours from the
  target partition are found.

This keeps single-partition vector queries index-accelerated instead of
brute-forcing the partition. Measured on the live `20,602`-doc partition
(3072 dims): warm p50 **≈ 9 ms**.

## Configuration

Bound from the `"Emulator:VectorIndex"` configuration section into
`VectorIndexOptions`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Master switch for index-accelerated vector search. |
| `ImplicitIndexing` | `true` | Auto-index embedding paths used in `VectorDistance` even without a declared policy. When `false`, only declared `VectorIndexes` paths are indexed. |
| `M` | `16` | HNSW graph connectivity (neighbours per node). |
| `EfConstruction` | `200` | Build-time search width — higher = better recall, slower build. |
| `EfSearch` | `100` | Query-time search width — higher = better recall, slower query. |
| `RebuildTombstoneRatio` | `0.25` | Tombstone fraction above which a shard is rebuilt. |
| `PartitionExactScanThreshold` | `4096` | Max live vectors in a partition for which a partition-scoped query exact-scans that partition instead of using the graph. |
| `BackgroundBuild` | `true` | Build the HNSW graph off the query thread; queries brute-force until it is ready, then switch to acceleration (avoids a multi-second first-query stall). |

Example `appsettings.json`:

```json
{
  "Emulator": {
    "VectorIndex": {
      "Enabled": true,
      "M": 16,
      "EfConstruction": 200,
      "EfSearch": 100
    }
  }
}
```

Index type drives the algorithm: `flat` → exact SIMD brute force;
`quantizedFlat` / `diskANN` → HNSW ANN.

## Build cost & tuning

The index is built **lazily on first vector query** for a path (and rebuilt when
tombstones accumulate). Build is the dominant one-time cost and scales with
`count × EfConstruction × log(count) × dims`:

- ~66–74 s for 20K × 3072
- ~8 min for 100K × 384

With `BackgroundBuild` (default **on**), this build runs **off the query thread**:
the first queries return immediately via an exact brute-force scan and
transparently switch to index acceleration once the graph is ready, so there is no
multi-second stall on the first query of a large container. Set `BackgroundBuild`
to `false` for deterministic behavior (e.g. in tests) where the first query should
block until the graph is built.

To reduce build time at a modest recall cost, lower `EfConstruction` (e.g. 100)
and/or `M`. To raise recall at a latency cost, raise `EfSearch`. Recall is
approximate by design; the test suite asserts a recall floor (≥ 0.8 vs
brute-force top‑K), not exact equality.

## Limitations / future work

- **First-query build latency** for large containers is mitigated by
  `BackgroundBuild` (queries brute-force until the graph is ready) but the index
  is still in-memory and rebuilt from stored docs on startup — not yet persisted
  to disk.
- **Resident memory** is un-quantized float32 (`dims × 4 B × count`);
  quantization is a later option.
- Vector index residency is per-process; there is no cross-process sharing.
