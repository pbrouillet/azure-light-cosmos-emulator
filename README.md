# Azure.Cosmos.LightEmulator

A lightweight, open-source Azure Cosmos DB emulator supporting NoSQL and MongoDB connectors.

## Features

- **NoSQL REST API** — Full Cosmos DB REST API compatibility on port 8081
- **MongoDB Wire Protocol** — Native MongoDB driver support on port 10255
- **Embedded Storage** — SurrealDB (RocksDB), SQLite, or In-Memory backends
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

## Storage Configuration

The emulator supports three storage backends, configurable via a side config file or CLI arguments:

| Backend    | Persistence | Best For                        |
|------------|-------------|---------------------------------|
| `SurrealDb` | ✅ Persistent (RocksDB) | Production-like testing (default) |
| `Sqlite`   | ✅ Persistent (single file) | Lightweight, portable storage   |
| `InMemory` | ❌ Ephemeral | Fast tests, CI/CD               |

### Config file (`emulator-config.json`)

Place an `emulator-config.json` file next to the executable:

```json
{
  "Emulator": {
    "Storage": "Sqlite",
    "DataDirectory": "C:\\MyData\\CosmosEmulator"
  }
}
```

### CLI override

CLI arguments take highest priority:

```bash
cosmos-emulator start --storage sqlite --data-dir ./my-data
cosmos-emulator start --storage inmemory
cosmos-emulator start                    # uses config file or defaults to SurrealDb
```

### Priority order

`appsettings.json` → `emulator-config.json` → CLI arguments (highest)

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

## Publishing a Single-File Release

Use `publish.ps1` to produce a self-contained, single-file executable with aggressive IL trimming (`TrimMode=full`):

```powershell
# Current platform (auto-detects RID)
./publish.ps1

# Cross-compile for a specific target
./publish.ps1 -Runtime linux-x64
./publish.ps1 -Runtime osx-arm64

# Disable trimming if you hit runtime issues
./publish.ps1 -Runtime win-x64 -NoTrim
```

The output is written to `publish/<runtime>/`. Tagged CI builds (`v*`) produce
single-file release assets automatically.

## Connection Strings

On startup, the emulator prints connection strings:

```
NoSQL Endpoint: https://localhost:8081
MongoDB Endpoint: mongodb://localhost:10255
Master Key: <generated-key>
```

## License

MIT
