using System.Text.Json.Serialization;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Azure.Cosmos.LightEmulator.Storage.SurrealDb;
using Microsoft.Extensions.Options;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class EmulatorAdminSettingsStore(
    SurrealDbConnectionManager connectionManager,
    IOptions<EmulatorOptions> emulatorOptions)
{
    private const string TableName = "admin_settings";
    private const string RecordKey = "entra";

    public async Task<EmulatorAdminSettings?> GetStoredSettingsAsync(CancellationToken ct = default) =>
        await connectionManager.Client.Select<EmulatorAdminSettings>(new RecordIdOfString(TableName, RecordKey), ct);

    public async Task<EmulatorAdminSettings> GetEffectiveSettingsAsync(CancellationToken ct = default)
    {
        var storedSettings = await GetStoredSettingsAsync(ct);
        var options = emulatorOptions.Value;

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

        var response = await connectionManager.Client.RawQuery(
            "UPSERT $recordId CONTENT $data",
            new Dictionary<string, object?>
            {
                ["recordId"] = new RecordIdOfString(TableName, RecordKey),
                ["data"] = settings
            },
            ct);

        response.EnsureAllOks();
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
