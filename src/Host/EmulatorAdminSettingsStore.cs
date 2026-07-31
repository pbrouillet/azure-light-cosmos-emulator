using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using Microsoft.Extensions.Options;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class EmulatorAdminSettingsStore
{
    private const string TableName = "admin_settings";
    private const string RecordKey = "entra";

    private readonly SurrealDbConnectionManager? _connectionManager;
    private readonly IOptions<EmulatorOptions> _emulatorOptions;
    private readonly ConcurrentDictionary<string, EmulatorAdminSettings> _inMemoryStore = new();

    public EmulatorAdminSettingsStore(
        IOptions<EmulatorOptions> emulatorOptions,
        SurrealDbConnectionManager? connectionManager = null)
    {
        _emulatorOptions = emulatorOptions;
        _connectionManager = connectionManager;
    }

    public async Task<EmulatorAdminSettings?> GetStoredSettingsAsync(CancellationToken ct = default)
    {
        if (_connectionManager is not null)
        {
            try
            {
                return await _connectionManager.Client.Select<EmulatorAdminSettings>(new RecordIdOfString(TableName, RecordKey), ct);
            }
            catch (Exception ex) when (SurrealDbErrors.IsMissingTable(ex))
            {
                return null;
            }
        }

        _inMemoryStore.TryGetValue(RecordKey, out var settings);
        return settings;
    }

    public async Task<EmulatorAdminSettings> GetEffectiveSettingsAsync(CancellationToken ct = default)
    {
        var storedSettings = await GetStoredSettingsAsync(ct);
        var options = _emulatorOptions.Value;

        return new EmulatorAdminSettings
        {
            EnableEntraId = storedSettings?.EnableEntraId ?? options.EnableEntraId,
            TenantId = Normalize(storedSettings?.TenantId ?? options.TenantId),
            ClientId = Normalize(storedSettings?.ClientId ?? options.ClientId)
        };
    }

    public async Task<EmulatorAdminSettings> UpdateSettingsAsync(
        bool enableEntraId,
        string? tenantId,
        string? clientId,
        CancellationToken ct = default)
    {
        var settings = new EmulatorAdminSettings
        {
            EnableEntraId = enableEntraId,
            TenantId = Normalize(tenantId),
            ClientId = Normalize(clientId)
        };

        if (_connectionManager is not null)
        {
            var response = await _connectionManager.Client.RawQuery(
                "UPSERT $recordId CONTENT $data",
                new Dictionary<string, object?>
                {
                    ["recordId"] = new RecordIdOfString(TableName, RecordKey),
                    ["data"] = settings
                },
                ct);
            response.EnsureAllOks();
        }
        else
        {
            _inMemoryStore[RecordKey] = settings;
        }

        return settings;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class EmulatorAdminSettings
{
    [JsonPropertyName("enableEntraId")]
    public bool EnableEntraId { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; set; }
}
