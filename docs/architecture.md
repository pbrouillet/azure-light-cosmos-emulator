# Architecture

## Overview

Azure.Cosmos.LightEmulator is a lightweight, open-source emulator for Azure Cosmos DB that supports both NoSQL (SQL API) and MongoDB connectors.

## Storage Layer

The emulator uses **SurrealDB embedded** with a **RocksDB KV** backend for durable storage. The mapping between Cosmos DB and SurrealDB concepts:

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
