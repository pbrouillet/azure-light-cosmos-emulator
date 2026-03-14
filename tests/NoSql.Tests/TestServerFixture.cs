using System.Globalization;
using System.Net.Http.Json;
using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Consistency;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.NoSql;
using Azure.Cosmos.LightEmulator.NoSql.Controllers;
using Azure.Cosmos.LightEmulator.NoSql.Middleware;
using Azure.Cosmos.LightEmulator.NoSql.StoredProcedures;
using Azure.Cosmos.LightEmulator.Storage.ChangeFeed;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Azure.Cosmos.LightEmulator.NoSql.Tests;

public sealed class TestServerFixture : IAsyncDisposable
{
    public const string KnownMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        "azure-light-cosmos-emulator-tests",
        Guid.NewGuid().ToString("N"));
    private readonly MasterKeyAuthProvider _authProvider = new(KnownMasterKey);

    private WebApplication? _app;
    private SurrealDbConnectionManager? _connectionManager;

    public HttpClient Client { get; private set; } = null!;

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

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDirectory);
        _connectionManager = new SurrealDbConnectionManager(_dataDirectory);
        await _connectionManager.InitializeAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = Directory.GetCurrentDirectory()
        });

        builder.WebHost.UseTestServer();
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(DatabasesController).Assembly);

        builder.Services.AddSingleton(_connectionManager);
        builder.Services.AddSingleton<IChangeFeedProvider, InMemoryChangeFeedProvider>();
        builder.Services.AddSingleton<SurrealDbDocumentStore>();
        builder.Services.AddSingleton<IDocumentStore>(sp => sp.GetRequiredService<SurrealDbDocumentStore>());
        builder.Services.AddSingleton<IQueryEngine, StubQueryEngine>();
        builder.Services.AddSingleton<IAuthProvider>(_ => new MasterKeyAuthProvider(KnownMasterKey));
        builder.Services.AddSingleton<IProgrammabilityEngine, JintProgrammabilityEngine>();
        builder.Services.AddSingleton<IConsistencyManager>(_ => new ConsistencyManager(ConsistencyLevel.Session));

        _app = builder.Build();
        _app.UseMiddleware<CosmosExceptionMiddleware>();
        _app.UseMiddleware<CosmosAuthMiddleware>();
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

        return (resourceType.ToLowerInvariant(), resourceLink.ToLowerInvariant());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_connectionManager is not null)
        {
            await _connectionManager.DisposeAsync();
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
}
