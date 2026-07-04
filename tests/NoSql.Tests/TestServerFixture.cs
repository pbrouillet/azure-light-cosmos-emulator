using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Consistency;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Infrastructure;
using Azure.Cosmos.LightEmulator.NoSql.Middleware;
using Azure.Cosmos.LightEmulator.NoSql.Query;
using Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;
using Azure.Cosmos.LightEmulator.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public sealed class TestServerFixture : IAsyncDisposable
{
    public const string KnownMasterKey = MasterKeyAuthProvider.DefaultMasterKey;

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "azure-light-cosmos-emulator-tests",
        Guid.NewGuid().ToString("N"));
    private readonly MasterKeyAuthProvider _authProvider = new(KnownMasterKey);

    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>
    /// Storage backend to use for tests. Defaults to SurrealDb for backward compatibility.
    /// </summary>
    public StorageType StorageType { get; init; } = StorageType.Sqlite;

    public T GetService<T>() where T : notnull
    {
        if (_app is null)
            throw new InvalidOperationException("The test server has not been initialized.");
        return _app.Services.GetRequiredService<T>();
    }

    public static async Task<TestServerFixture> CreateAsync()
    {
        var fixture = new TestServerFixture();
        await fixture.InitializeAsync();
        return fixture;
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string requestPath, object? body = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);

        if (Client.BaseAddress is null)
        {
            throw new InvalidOperationException("The test server has not been initialized.");
        }

        var request = new HttpRequestMessage(method, new Uri(Client.BaseAddress, requestPath));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public IReadOnlyDictionary<string, string> CreateAuthHeaders(HttpMethod method, string requestPath, DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);

        var date = (utcNow ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
        var (resourceType, resourceLink) = ExtractResourceInfo(requestPath);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CosmosHeaders.Authorization] = _authProvider.GenerateAuthHeader(method.Method, resourceType, resourceLink, date),
            ["x-ms-date"] = date,
            ["x-ms-version"] = CosmosHeaders.CurrentServiceVersion
        };
    }

    public HttpClient CreateUnauthenticatedClient()
    {
        if (_app is null)
        {
            throw new InvalidOperationException("The test server has not been initialized.");
        }

        var client = _app.GetTestServer().CreateClient();
        client.BaseAddress = new Uri("http://localhost");
        return client;
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDirectory);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = Directory.GetCurrentDirectory()
        });

        builder.WebHost.UseTestServer();
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = null;
                options.JsonSerializerOptions.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
            })
            .AddApplicationPart(typeof(DatabasesController).Assembly);

        builder.Services.AddEmulatorStorage(StorageType, _dataDirectory);
        builder.Services.AddSingleton<EmulatorRuntimeState>();
        builder.Services.AddSingleton<IEmulatorInfoService, FakeEmulatorInfoService>();
        builder.Services.AddSingleton<IQueryEngine, CosmosQueryEngine>();
        builder.Services.AddSingleton<QueryExplainService>();
        builder.Services.AddSingleton<IQueryTelemetryRecorder, QueryTelemetryRecorder>();
        builder.Services.AddSingleton<IQueryExecutionLimiter, QueryExecutionLimiter>();
        builder.Services.AddSingleton<IndexValidationService>();
        builder.Services.AddSingleton<DmlCommandService>();
        builder.Services.AddSingleton<IAuthProvider>(_ => new MasterKeyAuthProvider(KnownMasterKey));
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.IProgrammabilityRecordStore>(sp =>
        {
            var surrealManager = sp.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>();
            if (surrealManager is not null)
                return new Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.SurrealDbProgrammabilityRecordStore(surrealManager);
            return new Azure.Cosmos.LightEmulator.NoSql.StoredProcedures.InMemoryProgrammabilityRecordStore();
        });
        builder.Services.AddSingleton<IProgrammabilityEngine, JintProgrammabilityEngine>();
        builder.Services.AddSingleton<IConsistencyManager>(_ => new ConsistencyManager(ConsistencyLevel.Session));
        builder.Services.AddSingleton<Azure.Cosmos.LightEmulator.Triggers.Engine.TriggerEngine>();
        builder.Services.AddSingleton<CosmosResponseHeaderService>();

        _app = builder.Build();

        // Initialize SurrealDB connection if that storage backend is active
        var surrealManager = _app.Services.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>();
        if (surrealManager is not null)
            await surrealManager.InitializeAsync();

        _app.UseMiddleware<CosmosExceptionMiddleware>();
        _app.UseMiddleware<CosmosAuthMiddleware>();
        _app.UseMiddleware<ConsistencyMiddleware>();

        _app.MapMethods("/", ["GET", "HEAD"], async (HttpContext context, CosmosResponseHeaderService responseHeaders, CancellationToken ct) =>
        {
            await responseHeaders.ApplyAsync(context.Response, new CosmosResponseHeaderOptions(), ct);
            var endpoint = $"{context.Request.Scheme}://{context.Request.Host}/";
            var location = new { name = "Local", databaseAccountEndpoint = endpoint };
            return Results.Json(new
            {
                _self = string.Empty,
                id = context.Request.Host.Host,
                _rid = context.Request.Host.Host,
                media = "/media/",
                addresses = "/addresses/",
                _dbs = "/dbs/",
                writableLocations = new[] { location },
                readableLocations = new[] { location },
                enableMultipleWriteLocations = false,
                userConsistencyPolicy = new { defaultConsistencyLevel = "Session" },
            });
        });
        _app.MapControllers();

        await _app.StartAsync();

        var serverHandler = _app.GetTestServer().CreateHandler();
        Client = new HttpClient(new CosmosAuthHandler(this, serverHandler))
        {
            BaseAddress = new Uri("http://localhost")
        };
    }

    private void ApplyAuthHeaders(HttpRequestMessage request)
    {
        var requestPath = request.RequestUri is null
            ? throw new InvalidOperationException("Request URI must be set before sending the request.")
            : request.RequestUri.IsAbsoluteUri
                ? request.RequestUri.PathAndQuery
                : request.RequestUri.OriginalString;

        foreach (var header in CreateAuthHeaders(request.Method, requestPath))
        {
            if (!request.Headers.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    private static (string resourceType, string resourceLink) ExtractResourceInfo(string requestPath)
    {
        var path = requestPath.Split('?', 2)[0];
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var resourceType = segments.Length switch
        {
            1 => segments[0],
            2 => segments[0],
            3 => segments[2],
            4 => segments[2],
            5 => segments[4],
            6 => segments[4],
            _ => segments[^1]
        };

        var resourceLink = segments.Length switch
        {
            1 => string.Empty,
            2 => string.Join('/', segments[..2]),
            3 => string.Join('/', segments[..2]),
            4 => string.Join('/', segments[..4]),
            5 => string.Join('/', segments[..4]),
            6 => string.Join('/', segments[..6]),
            _ => string.Join('/', segments)
        };

        return (resourceType.ToLowerInvariant(), resourceLink);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        if (_app is not null)
        {
            // Dispose SurrealDB connection manager if present
            var surrealManager = _app.Services.GetService<Azure.Cosmos.LightEmulator.Storage.SurrealDb.SurrealDbConnectionManager>();
            if (surrealManager is not null)
                await surrealManager.DisposeAsync();

            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (Directory.Exists(_dataDirectory))
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class CosmosAuthHandler(TestServerFixture fixture, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fixture.ApplyAuthHeaders(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FakeEmulatorInfoService : IEmulatorInfoService
    {
        public Task<JsonObject> GetInfoAsync(CancellationToken ct = default) => Task.FromResult(new JsonObject());

        public Task<JsonObject> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new JsonObject());

        public Task<JsonObject> UpdateSettingsAsync(bool enableEntraId, string? tenantId, string? clientId, CancellationToken ct = default) =>
            Task.FromResult(new JsonObject());
    }
}
