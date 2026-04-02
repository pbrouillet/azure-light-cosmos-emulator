using Azure.Cosmos.LightEmulator.Auth.EntraId;
using Azure.Cosmos.LightEmulator.Auth.KeyAuth;
using Azure.Cosmos.LightEmulator.Core.Interfaces;
using Azure.Cosmos.LightEmulator.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Azure.Cosmos.LightEmulator.Host;

public sealed class EmulatorAuthProvider(
    IOptions<EmulatorOptions> emulatorOptions,
    EmulatorAdminSettingsStore adminSettingsStore) : IAuthProvider
{
    private readonly MasterKeyAuthProvider _masterKeyAuthProvider = new(emulatorOptions.Value.MasterKey);

    public async Task<AuthResult> ValidateAsync(
        string authHeader,
        string verb,
        string resourceType,
        string resourceLink,
        string dateHeader,
        CancellationToken ct = default)
    {
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var settings = await adminSettingsStore.GetEffectiveSettingsAsync(ct);
            var entraIdAuthProvider = new EntraIdAuthProvider(settings.EnableEntraId, settings.TenantId, settings.ClientId);
            return await entraIdAuthProvider.ValidateAsync(authHeader, verb, resourceType, resourceLink, dateHeader, ct);
        }

        return await _masterKeyAuthProvider.ValidateAsync(authHeader, verb, resourceType, resourceLink, dateHeader, ct);
    }
}
