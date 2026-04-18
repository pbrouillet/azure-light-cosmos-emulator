# Copilot Instructions — Azure.Cosmos.LightEmulator

## Build, Test & Lint

```bash
# Full solution build
dotnet build Azure.Cosmos.LightEmulator.slnx

# Run all tests
dotnet test Azure.Cosmos.LightEmulator.slnx

# Run a single test project
dotnet test tests\Core.Tests

# Run a single test by name
dotnet test tests\Core.Tests --filter "FullyQualifiedName~ConsistencyManagerTests.DefaultLevel"

# Explorer SPA (React/Vite)
cd src\Explorer
npm run dev          # dev server with hot reload (proxies /dbs → localhost:8081)
npm run build        # production build → src\Host\wwwroot\explorer\
npm run lint         # ESLint

# Docker
docker build -f docker\Dockerfile .
docker compose -f docker\docker-compose.yml up
```

## Architecture

The emulator implements the Azure Cosmos DB REST API (port 8081) and MongoDB wire protocol (port 10255), backed by SurrealDB/RocksDB storage.

### Project dependency graph

```
Cli → Host → NoSql  → Core
                    → Storage → Core
                    → Auth    → Core
            → MongoDB → Core, Storage, Auth
            → Triggers → Core, Storage
```

- **Core** — Domain models, interfaces (`IDocumentStore`, `IQueryEngine`, `IAuthProvider`, `IChangeFeedProvider`, `IConsistencyManager`, `IProgrammabilityEngine`), and the `ConsistencyManager` implementation. Pure library with no external dependencies.
- **Storage** — `SurrealDbDocumentStore` implements `IDocumentStore` using in-memory `ConcurrentDictionary` (SurrealDB/RocksDB wiring ready). `InMemoryChangeFeedProvider` tracks document changes with LSNs.
- **Auth** — Three `IAuthProvider` implementations: `MasterKeyAuthProvider` (HMAC-SHA256), `EntraIdAuthProvider` (OIDC/JWT), `ResourceTokenProvider`. `CompositeAuthProvider` chains them.
- **NoSql** — ASP.NET controllers matching the Cosmos DB REST API, plus `CosmosAuthMiddleware`, `CosmosExceptionMiddleware`, `CosmosQueryEngine` (SQL parser), and `JintProgrammabilityEngine` (stored procedures via Jint).
- **MongoDB** — TCP server (`MongoDbServer`) on port 10255 speaking MongoDB wire protocol (OP_MSG, OP_QUERY).
- **Triggers** — Quartz.NET-based trigger scheduling with Jint execution.
- **Host** — ASP.NET Core entry point. Registers all services, configures middleware pipeline, serves Explorer static files at `/explorer`.
- **Cli** — `System.CommandLine` tool (`cosmos-emulator start|stop|reset|status|export|import`).
- **Explorer** — React 19 + TypeScript + Vite + TailwindCSS SPA. Uses Monaco Editor for JSON (documents), SQL (queries), and JavaScript (sprocs/triggers/UDFs). Builds to `src/Host/wwwroot/explorer/`.

### Middleware pipeline order (Host)

```
CosmosExceptionMiddleware → CosmosAuthMiddleware → Controllers
                                                 → Static files (/explorer)
```

Auth is skipped for `/explorer`, `/`, and `/health` paths.

### Configuration

All emulator settings flow through `EmulatorOptions` (bound from the `"Emulator"` config section):
- `Port` (8081), `MongoPort` (10255), `DataDirectory`, `MasterKey`, `EnableEntraId`, `ConsistencyLevel` ("Session"), `EnableSsl`, `EnableExplorer`

## Key Conventions

### .NET

- **.NET 10** pinned in `global.json`, target framework `net10.0`
- **Central package management** — all versions in `Directory.Packages.props`. Never add `Version=` to `PackageReference` in `.csproj` files.
- **Nullable enabled, warnings as errors** — set in `Directory.Build.props`
- **Root namespace**: `Azure.Cosmos.LightEmulator`. Projects append their name (e.g., `Azure.Cosmos.LightEmulator.Core.Models`)
- **ASP.NET types in class libraries** — NoSql, Auth, and MongoDB projects use `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (not `Microsoft.NET.Sdk.Web`)

### REST API pattern

Controllers in `src/NoSql/Controllers/` follow the Cosmos DB REST URL structure:
- `/dbs`, `/dbs/{dbId}/colls`, `/dbs/{dbId}/colls/{collId}/docs`, `.../sprocs`, `.../triggers`, `.../udfs`
- Cosmos-specific headers (`x-ms-*`) are defined as constants in `CosmosHeaders`
- All responses include `x-ms-request-charge`, `x-ms-activity-id`, `x-ms-serviceversion`
- Errors return `{ code, message }` JSON with appropriate HTTP status codes
- Exceptions use `CosmosEmulatorException` static factory methods (`.NotFound()`, `.Conflict()`, `.BadRequest()`, etc.)

### Storage abstraction

All data access goes through `IDocumentStore`. The implementation uses `ConcurrentDictionary` with composite keys:
- Databases: keyed by `id`
- Containers: keyed by `{databaseId}/{containerId}`
- Documents: keyed by `{databaseId}/{containerId}/{partitionKeyHeaderValue}/{documentId}`

Document writes increment a global LSN and record changes via `IChangeFeedProvider`.

### Auth header format

Master key auth uses the Cosmos DB signature format:
```
type=master&ver=1.0&sig={HMAC-SHA256 of "verb\nresourceType\nresourceLink\ndate\n\n"}
```

The known test master key is: `C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==`

### Explorer (React)

- Vite base path: `/explorer/` — all assets served from this prefix
- Dev proxy: Vite forwards `/dbs` requests to `http://localhost:8081`
- Build output: `src/Host/wwwroot/explorer/` (committed for Docker builds)
- Monaco Editor languages: JSON (DocumentEditor), SQL (QueryEditor), JavaScript (ProgrammabilityEditor)
- Data fetching: `@tanstack/react-query` with `cosmosClient` singleton
- Routing: React Router v7, routes follow `/db/:dbId/container/:collId/...` pattern

### Fluent UI v9 — Overlays, Dialogs & Portals

Fluent UI v9 components that render portals (dropdowns, comboboxes, dialogs) need careful handling to avoid content-disappearing bugs:

- **Never use custom backdrop/panel overlays with manual z-index for popover-style panels.** Fluent UI `Combobox`, `Dropdown`, and `Select` render their listboxes through portals at the `FluentProvider` root. A custom `position: fixed; z-index: 1000` backdrop will sit above these portals and intercept clicks, making dropdowns appear broken. Use `Popover` + `PopoverSurface` + `PopoverTrigger` instead — they share the same portal z-index stack and nest correctly with other Fluent portals.

- **Always use `modalType="non-modal"` on `<Dialog>` components with an explicit backdrop.** Modal Dialogs (`modalType="modal"`, the default) trigger three side-effects that can cause content to visually disappear or become unresponsive: (1) `useDisableBodyScroll` adds `overflow-y: hidden/clip` plus `scrollbar-gutter: stable` to `<html>` and `<body>`, which can break `height: 100%` layout chains; (2) tabster's modalizer walks `document.body` and sets `aria-hidden="true"` on all sibling elements, which may interact poorly with custom CSS or browser extensions; (3) legacy focus trapping intercepts keyboard events and can cause focus-loss glitches. Using `modalType="non-modal"` disables all three mechanisms. Add an explicit `backdrop` prop to `<DialogSurface>` to restore the visual overlay and click-outside-to-close behavior:
  ```tsx
  <Dialog modalType="non-modal" open={isOpen} onOpenChange={(_, d) => setIsOpen(d.open)}>
    <DialogSurface backdrop={{ onClick: () => setIsOpen(false) }}>
      <DialogBody>
        <DialogTitle>Title</DialogTitle>
        <DialogContent>...</DialogContent>
        <DialogActions>
          <Button onClick={() => setIsOpen(false)}>Cancel</Button>
          <Button appearance="primary">OK</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
  ```

- **Pattern for floating panels with nested interactive portals:**
  ```tsx
  <Popover positioning="below-start" trapFocus open={isOpen} onOpenChange={(_, d) => setIsOpen(d.open)}>
    <PopoverTrigger disableButtonEnhancement>
      <Button>Open panel</Button>
    </PopoverTrigger>
    <PopoverSurface>
      {/* Combobox/Dropdown inside here will portal correctly */}
      <Combobox>
        <Option>Item</Option>
      </Combobox>
    </PopoverSurface>
  </Popover>
  ```

## Testing Patterns

- **Framework**: xUnit with FluentAssertions and Moq
- **Integration tests** use `TestServerFixture` (`tests/NoSql.Tests/TestServerFixture.cs`) which spins up an in-process `WebApplication` with `TestServer`, registers all services, and provides an `HttpClient` with auto-injected HMAC auth headers
- **Unit tests** directly instantiate the class under test (no DI container)
- **SDK parity tests** (`tests/Parity.Tests/`) clone the official Azure Cosmos .NET SDK test suite via MSBuild target (`CloneSdkTests.targets`) and run tests against the emulator. Skip with `-p:SkipCloneSdkTests=true`

### Three DI registration surfaces — keep in sync

There are **three independent** service registration paths. Any new service or controller added to one must be added to the others or you'll get 500 `Unable to resolve service` errors or 404s:

1. **`src/Host/Program.cs`** — production startup (Serilog, explorer static files, throughput enforcement)
2. **`src/Host/HostApplication.cs`** — CLI / test startup (InMemoryChangeFeedProvider, no throughput middleware, Swagger in dev)
3. **`tests/NoSql.Tests/TestServerFixture.cs`** — test fixture (minimal DI, test-specific fakes)

### Adding a new integration test

```csharp
public class MyTests : IAsyncLifetime
{
    private TestServerFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await TestServerFixture.CreateAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task MyOperation_ShouldSucceed()
    {
        var request = _fixture.CreateRequest(HttpMethod.Post, "/dbs", new { id = "testdb" });
        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
```

## Query Engine — Adding New SQL Functions

The query engine (`src/NoSql/Query/CosmosQueryEngine.cs`) is a hand-rolled SQL parser/evaluator. Target API version: `2024-11-30`.

### Parser limitation: no inline JSON object literals

The expression parser supports array literals (`[1, 2, 3]`), path references (`c.field`), and parameters (`@param`) — but **not** inline JSON object literals (`{"type": "Point", ...}`). Tests for functions that take GeoJSON or complex objects must pass them via document fields or `@parameters`:

```csharp
// ✅ Correct — use document fields
await SeedDocumentsAsync(store, CreateDocument("doc-1", "t1", d =>
{
    d["location"] = MakePoint(-122.12, 47.67);
    d["boundary"] = MakePolygon((-122.15, 47.65), ...);
}));
var result = await engine.ExecuteQueryAsync("db", "coll",
    "SELECT ST_DISTANCE(c.location, c.boundary) AS dist FROM c");

// ✅ Also correct — use parameters
var parameters = new Dictionary<string, object?> { ["@target"] = MakePoint(-122.11, 47.67) };
var result = await engine.ExecuteQueryAsync("db", "coll",
    "SELECT ST_DISTANCE(c.location, @target) AS dist FROM c", parameters);

// ❌ Wrong — parser will throw "Unsupported character '{'"
var result = await engine.ExecuteQueryAsync("db", "coll",
    """SELECT ST_DISTANCE(c.location, {"type":"Point","coordinates":[-122.11,47.67]}) FROM c""");
```

### Steps to add a new built-in function

1. Add a case to the `EvaluateBuiltInFunction` switch (around line 1340) — or to `EvaluateFunction` if the function needs container metadata (like `VectorDistance`)
2. Implement a `private static object? EvaluateMyFunction(IReadOnlyList<object?> arguments)` method
3. Return `UndefinedValue` for invalid/missing inputs (not `null` — `null` is a valid Cosmos DB value)
4. Write tests using document fields or parameters (not inline JSON objects)
5. Update `docs/api-compatibility.md` and `docs/architecture.md`

### Known intentional limitations

- **Request Units**: Formula-based estimation, not real metering (`RuCostCalculator.cs`)
- **Indexing**: Metadata is stored and round-tripped but not used for query optimization — all queries scan all documents
- **Consistency levels**: All five accepted; only Session tokens actually enforced (single-node emulator)
- **Spatial indexes**: Stored on containers but not used for query acceleration
- **Stored procedure context**: Limited `getContext()` API via Jint (no `collection.createDocument()`, etc.)

## Explorer (React) — Defensive Coding

All Explorer React components must use optional chaining (`?.`) and nullish coalescing (`?? defaultValue`) when accessing API response fields. Backend responses may omit fields or return `null`, and mapping over arrays with missing numeric fields (e.g., `.toFixed()`, `.toUpperCase()`) has caused `TypeError` crashes in multiple sessions.
