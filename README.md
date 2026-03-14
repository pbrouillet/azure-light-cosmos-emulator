# Azure.Cosmos.LightEmulator

A lightweight, open-source Azure Cosmos DB emulator supporting NoSQL and MongoDB connectors.

## Features

- **NoSQL REST API** — Full Cosmos DB REST API compatibility on port 8081
- **MongoDB Wire Protocol** — Native MongoDB driver support on port 10255
- **Embedded Storage** — SurrealDB with RocksDB persistence
- **Authentication** — Master key (HMAC-SHA256) and EntraID (Azure AD) support
- **Consistency Levels** — All five Cosmos DB consistency levels
- **Change Feed** — Pull and push model support
- **Stored Procedures / Triggers / UDFs** — JavaScript execution via Jint
- **Explorer UI** — React-based data explorer at `/explorer`
- **CLI** — `cosmos-emulator start|stop|reset|status|export|import`
- **SDK Parity Tests** — Automated validation against official Azure Cosmos .NET SDK tests

## Quick Start

```bash
# Run via CLI
dotnet run --project src/Cli -- start

# Run via Docker
docker compose up
```

## Development

```bash
# Restore & build
dotnet restore
dotnet build

# Run tests
dotnet test

# Run explorer dev server
cd src/Explorer && npm run dev
```

## Connection Strings

On startup, the emulator prints connection strings:

```
NoSQL Endpoint: https://localhost:8081
MongoDB Endpoint: mongodb://localhost:10255
Master Key: <generated-key>
```

## License

MIT
