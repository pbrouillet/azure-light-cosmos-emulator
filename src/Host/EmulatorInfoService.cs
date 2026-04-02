using System.Text.Json.Nodes;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class EmulatorInfoService(
    IOptions<EmulatorOptions> emulatorOptions,
    IDocumentStore documentStore,
    RuTracker ruTracker,
    EmulatorAdminSettingsStore adminSettingsStore) : IEmulatorInfoService
{
    private const string EmulatorName = "Azure Cosmos DB Light Emulator";

    public async Task<JsonObject> GetInfoAsync(CancellationToken ct = default)
    {
        var options = emulatorOptions.Value;
        var adminSettings = await adminSettingsStore.GetEffectiveSettingsAsync(ct);
        var noSqlEndpoint = GetNoSqlEndpoint(options);

        return new JsonObject
        {
            ["name"] = EmulatorName,
            ["version"] = GetVersion(),
            ["endpoints"] = new JsonObject
            {
                ["noSql"] = noSqlEndpoint,
                ["mongoDb"] = $"mongodb://localhost:{options.MongoPort}",
                ["explorer"] = options.EnableExplorer ? $"{noSqlEndpoint}/explorer" : null
            },
            ["connectionString"] = $"AccountEndpoint={noSqlEndpoint};AccountKey={options.MasterKey};",
            ["masterKey"] = options.MasterKey ?? string.Empty,
            ["configuration"] = new JsonObject
            {
                ["port"] = options.Port,
                ["mongoPort"] = options.MongoPort,
                ["storage"] = options.Storage,
                ["dataDirectory"] = options.DataDirectory,
                ["consistencyLevel"] = options.ConsistencyLevel,
                ["enableSsl"] = options.EnableSsl,
                ["enableExplorer"] = options.EnableExplorer,
                ["enableEntraId"] = adminSettings.EnableEntraId,
                ["tenantId"] = adminSettings.TenantId,
                ["clientId"] = adminSettings.ClientId
            }
        };
    }

    public async Task<JsonObject> GetStatsAsync(CancellationToken ct = default)
    {
        var options = emulatorOptions.Value;
        var databases = await documentStore.ListDatabasesAsync(ct);
        var containerCount = 0;

        foreach (var database in databases.Resources)
        {
            var containers = await documentStore.ListContainersAsync(database.Id, ct);
            containerCount += containers.Count;
        }

        return new JsonObject
        {
            ["totalRequestUnits"] = Math.Round(ruTracker.TotalRequestUnits, 2),
            ["totalRequests"] = ruTracker.TotalRequests,
            ["databaseCount"] = databases.Count,
            ["containerCount"] = containerCount,
            ["documentCount"] = 0,
            ["dataDirectory"] = options.DataDirectory,
            ["dataSizeBytes"] = GetDirectorySize(options.DataDirectory),
            ["uptimeSeconds"] = (long)ruTracker.UptimeSeconds
        };
    }

    public async Task<JsonObject> UpdateSettingsAsync(bool enableEntraId, string? tenantId, string? clientId, CancellationToken ct = default)
    {
        await adminSettingsStore.UpdateSettingsAsync(enableEntraId, tenantId, clientId, ct);
        return await GetInfoAsync(ct);
    }

    private static string GetNoSqlEndpoint(EmulatorOptions options)
    {
        var scheme = options.EnableSsl ? "https" : "http";
        return $"{scheme}://localhost:{options.Port}";
    }

    private static string GetVersion()
    {
        var version = typeof(EmulatorInfoService).Assembly.GetName().Version;
        return version is null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private static long GetDirectorySize(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return 0;
        }

        return Directory
            .EnumerateFiles(dataDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            })
            .Sum(path => new FileInfo(path).Length);
    }
}
