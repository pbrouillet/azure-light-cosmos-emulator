using System.Text.Json.Nodes;

namespace Azure.Cosmos.LightEmulator.Core.Interfaces;

public interface IEmulatorInfoService
{
    Task<JsonObject> GetInfoAsync(CancellationToken ct = default);

    Task<JsonObject> GetStatsAsync(CancellationToken ct = default);

    Task<JsonObject> UpdateSettingsAsync(bool enableEntraId, string? tenantId, string? clientId, CancellationToken ct = default);
}
