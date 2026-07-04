# Architecture

## Overview

Azure.Cosmos.LightEmulator is a lightweight, open-source emulator for Azure Cosmos DB that supports both NoSQL (SQL API) and MongoDB connectors.

## Storage Layer

The emulator supports three **pluggable storage backends**, selected via configuration:

| Backend | Engine | Persistence | Use Case |
|---|---|---|---|
| **Sqlite** (default) | Microsoft.Data.Sqlite | ✅ Persistent (single file) | Lightweight, portable |
| **SurrealDb** | SurrealDB embedded + RocksDB KV | ✅ Persistent | Production-like testing |
| **InMemory** | ConcurrentDictionary | ❌ Ephemeral | Fast tests, CI/CD |

All backends implement the `IDocumentStore` interface (31 methods) and can be selected at startup:
- **Config file**: Place `emulator-config.json` next to the executable with `{ "Emulator": { "Storage": "Sqlite" } }`
- **CLI**: `cosmos-emulator start --storage sqlite`
- **Priority**: `appsettings.json` → `emulator-config.json` → CLI arguments

Storage services are registered via `StorageServiceRegistration.AddEmulatorStorage()` which dispatches `IDocumentStore`, `IChangeFeedProvider`, `IActivityStore`, and `IQueryTelemetryStore` based on the configured backend.

### SurrealDB backend

The SurrealDB mapping between Cosmos DB and SurrealDB concepts:

| Cosmos DB | SurrealDB | Notes |
|---|---|---|
| Database | Namespace | Isolated data scope |
| Container | Table | Document collection |
| Document | Record | JSON document with system properties |
| Partition Key | Indexed field | Used for data distribution |

## Authentication

Two authentication mechanisms are supported:

1. **Master Key** — HMAC-SHA256 signature validation per the Cosmos DB REST API spec
2. **EntraID** — Azure AD OIDC/JWT bearer token validation

The `CompositeAuthProvider` tries each provider in order and returns the first successful result.

## NoSQL REST API

The emulator implements the Cosmos DB REST API on port 8081:

- `/dbs` — Database operations
- `/dbs/{dbId}/colls` — Container operations  
- `/dbs/{dbId}/colls/{collId}/docs` — Document CRUD and queries
- `/dbs/{dbId}/colls/{collId}/sprocs` — Stored procedures
- `/dbs/{dbId}/colls/{collId}/triggers` — Triggers
- `/dbs/{dbId}/colls/{collId}/udfs` — User-defined functions

## MongoDB Wire Protocol

A TCP server on port 10255 speaks the MongoDB wire protocol (OP_MSG, OP_QUERY, OP_REPLY), allowing native MongoDB drivers to connect directly.

## Query Engine

The Cosmos SQL query engine supports:
- SELECT, FROM, WHERE, JOIN, ORDER BY, TOP, OFFSET...LIMIT
- Aggregate functions (COUNT, SUM, AVG, MIN, MAX)
- Built-in functions (ARRAY_CONTAINS, CONTAINS, etc.)
- Parameterized queries (@param)
- Cross-partition queries
- Subqueries: EXISTS, IN (SELECT ...), scalar subqueries in SELECT/WHERE/FROM
- AS aliases in SELECT projections
- Vector search: `VectorDistance` (cosine, dotproduct, euclidean), index-accelerated via an in-memory **HNSW ANN index** (see [`vector-search.md`](vector-search.md)); honors vector embedding policy and vector indexes
- Spatial functions: ST_DISTANCE, ST_WITHIN, ST_INTERSECTS, ST_ISVALID, ST_ISVALIDDETAILED, ST_AREA (GeoJSON Point, Polygon, MultiPolygon, LineString)

## Change Feed

Document changes are tracked with logical sequence numbers (LSNs). Supports:
- Pull model with continuation tokens
- Push model (Change Feed Processor pattern)
- Full-fidelity mode (all versions and deletes)

## Explorer

React/TypeScript/Vite SPA served at `/explorer` with:
- Database/Container/Document tree navigation
- Monaco-based JSON document editor
- SQL query workbench
- Stored procedure/trigger/UDF management
