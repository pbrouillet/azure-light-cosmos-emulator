using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Models;
using Azure.Cosmos.LightEmulator.Host;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Azure.Cosmos.LightEmulator.Parity;

public abstract class ParityTestBase : IAsyncLifetime
{
    private WebApplication? _app;
    private MasterKeyAuthProvider? _authProvider;
    private string? _dataDirectory;

    protected CosmosClient Client { get; private set; } = null!;

    protected string MasterKey { get; private set; } = string.Empty;

    protected Uri Endpoint { get; } = new("http://localhost:8081/");

    public async Task InitializeAsync()
    {
        MasterKey = MasterKeyAuthProvider.GenerateMasterKey();
        _authProvider = new MasterKeyAuthProvider(MasterKey);
        _dataDirectory = Path.Combine(Path.GetTempPath(), "azure-light-cosmos-emulator", Guid.NewGuid().ToString("N"));

        var builder = HostApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{EmulatorOptions.SectionName}:DataDirectory"] = _dataDirectory,
            [$"{EmulatorOptions.SectionName}:EnableSsl"] = bool.FalseString,
            [$"{EmulatorOptions.SectionName}:EnableExplorer"] = bool.FalseString,
            [$"{EmulatorOptions.SectionName}:MasterKey"] = MasterKey,
            [$"{EmulatorOptions.SectionName}:Port"] = Endpoint.Port.ToString()
        });

        _app = HostApplication.Build(builder);
        await _app.StartAsync();

        Client = new CosmosClient(
            Endpoint.ToString(),
            MasterKey,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                HttpClientFactory = () =>
                {
                    var client = _app.GetTestClient();
                    client.BaseAddress = Endpoint;
                    return client;
                }
            });
    }

    protected async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string resourcePath,
        string resourceType,
        string resourceLink,
        object? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (_app is null || _authProvider is null)
        {
            throw new InvalidOperationException("The parity test host has not been initialized.");
        }

        var request = new HttpRequestMessage(method, resourcePath);
        var date = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        request.Headers.Add("x-ms-date", date);
        request.Headers.Add(CosmosHeaders.Authorization, _authProvider.GenerateAuthHeader(method.Method, resourceType, resourceLink.ToLowerInvariant(), date));
        request.Headers.Add("x-ms-version", CosmosHeaders.CurrentServiceVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var client = _app.GetTestClient();
        client.BaseAddress = Endpoint;
        return await client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_app is not null)
        {
            // Explicitly dispose the SurrealDB connection manager first so RocksDB
            // releases its file locks before we attempt to delete the data directory.
            try
            {
                var connectionManager = _app.Services.GetService<SurrealDbConnectionManager>();
                if (connectionManager is not null)
                {
                    await connectionManager.DisposeAsync();
                }
            }
            catch (Exception)
            {
                // Best-effort — the manager may already be disposed.
            }

            try
            {
                await _app.StopAsync();
            }
            catch (Exception)
            {
                // CancellationTokenSource disposal race during shutdown is benign.
            }

            try
            {
                await _app.DisposeAsync();
            }
            catch (AggregateException)
            {
                // Suppress disposal exceptions from CTS/SurrealDB cleanup race.
            }
        }

        if (_dataDirectory is not null && Directory.Exists(_dataDirectory))
        {
            // RocksDB may still hold file locks briefly after disposal; retry with back-off
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(_dataDirectory, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 9)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)));
                }
                catch (UnauthorizedAccessException) when (attempt < 9)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)));
                }
            }
        }
    }
}
