using Azure.Cosmos.LightEmulator.Core.Interfaces;

namespace Azure.Cosmos.LightEmulator.Auth;

/// <summary>
/// Composite auth provider that tries multiple auth providers in order.
/// </summary>
public class CompositeAuthProvider : IAuthProvider
{
    private readonly IReadOnlyList<IAuthProvider> _providers;

    public CompositeAuthProvider(IEnumerable<IAuthProvider> providers)
    {
        _providers = providers.ToList();
    }

    public async Task<AuthResult> ValidateAsync(
        string authHeader,
        string verb,
        string resourceType,
        string resourceLink,
        string dateHeader,
        CancellationToken ct = default)
    {
        // Try each provider in order; return first success
        AuthResult? lastFailure = null;

        foreach (var provider in _providers)
        {
            var result = await provider.ValidateAsync(authHeader, verb, resourceType, resourceLink, dateHeader, ct);
            if (result.IsAuthenticated)
                return result;
            lastFailure = result;
        }

        return lastFailure ?? AuthResult.Failure("No authentication providers configured.");
    }
}
